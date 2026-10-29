using System.Collections.Immutable;
using System.Text.Json;
using ContractScribe.Core;
using static ContractScribe.GitHub.Transport.GitHubResponseReader;

namespace ContractScribe.GitHub.Transport;

internal sealed record GitHubCampaignRepositoryFacts(GitHubRepositoryIdentity Identity,
    bool Private, bool Archived, bool Disabled, string? ViewerPermission) : GitHubValue;
internal sealed record GitHubCampaignSnapshot(GitHubCampaignRepositoryFacts Repository, GitHubRef Target,
    GitHubRef? Coordination, GitHubPullRequestSet PullRequests, GitHubRepository? PermissionSample) : GitHubValue;

internal sealed partial class GitHubApiClient
{
    internal const string CampaignSnapshotDocument = """
        query CampaignPlatform($owner:String!,$name:String!,$target:String!,$coord:String!,$after:String){
          repository(owner:$owner,name:$name,followRenames:false){
            ...CampaignRepository isPrivate isArchived isDisabled viewerPermission
            base:ref(qualifiedName:$target){id prefix name target{__typename oid}}
            coord:ref(qualifiedName:$coord){id prefix name target{__typename oid}}
            pullRequests(first:100,after:$after,orderBy:{field:CREATED_AT,direction:ASC}){
              totalCount pageInfo{hasNextPage endCursor}
              nodes{id fullDatabaseId number state isDraft merged mergedAt closedAt createdAt title body maintainerCanModify
                headRefName headRefOid baseRefName baseRefOid
                headRepository{...CampaignRepository} baseRepository{...CampaignRepository}
                author{...CampaignActor}
              }
            }
          }
        }
        fragment CampaignRepository on Repository{
          id fullDatabaseId name owner{...CampaignActor}
        }
        fragment CampaignActor on Actor{
          __typename login ... on Node{id}
          ... on User{databaseId} ... on Bot{databaseId}
          ... on Organization{databaseId} ... on Mannequin{databaseId}
        }
        """;

    // Fields share a response, not a transactional snapshot. The caller must
    // close its window with a distinct actual read. One logical gate only.
    // The REST permission vector is not reconstructed
    // from viewerPermission, which can be null for an authenticated GitHub App.
    internal async ValueTask<GitHubApiResult<GitHubCampaignSnapshot>> ReadCampaignSnapshotAsync(
        bool sampleRepositoryPermissions, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var dispatched = false;
        try
        {
            GitHubRepository? sample = null;
            if (sampleRepositoryPermissions)
            {
                var permission = await GetRepositoryAsync(deadline.Token).ConfigureAwait(false);
                dispatched |= permission.Delivery != GitHubDelivery.NotDispatched;
                if (permission.Value is null) return Failure(permission);
                sample = permission.Value;
            }
            CampaignPage? first = null;
            var items = new Dictionary<long, GitHubPullRequest>();
            var numbers = new HashSet<int>();
            var nodes = new HashSet<string>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            DateTimeOffset? latest = null;
            long bytes = 0;
            var observed = 0;
            for (var page = 1; page <= MaximumPages; page++)
            {
                var after = cursor;
                var response = await RunAsync<CampaignPage>(() =>
                {
                    var body = Encode(writer =>
                    {
                        writer.WriteString("query", CampaignSnapshotDocument);
                        writer.WriteStartObject("variables");
                        writer.WriteString("owner", readAuthority.RepositoryOwner);
                        writer.WriteString("name", readAuthority.RepositoryName);
                        writer.WriteString("target", readAuthority.TargetRef);
                        writer.WriteString("coord", GitHubPublicationFactory.CreateCoordinationRef(readAuthority));
                        writer.WriteString("after", after);
                        writer.WriteEndObject();
                    });
                    return new("graphql", ReadCampaignPage, body, Status: 200);
                }, deadline.Token).ConfigureAwait(false);
                dispatched |= response.Delivery != GitHubDelivery.NotDispatched;
                if (response.Value is not { } current) return Failure(response);
                first ??= current;
                Require(current.Repository == first.Repository && current.Target == first.Target
                    && current.Coordination == first.Coordination && current.Total == first.Total);
                if (sample is not null)
                    Require(current.Repository.Identity == sample.Identity && current.Repository.Private == sample.Private
                        && current.Repository.Archived == sample.Archived && current.Repository.Disabled == sample.Disabled);
                bytes = checked(bytes + current.Bytes);
                observed = checked(observed + current.Items.Length);
                Require(bytes <= MaximumCollectionBytes && observed <= MaximumPullRequests);
                foreach (var item in current.Items)
                {
                    if (items.TryGetValue(item.Id, out var previous)) Require(previous == item);
                    else
                    {
                        Require(numbers.Add(item.Number) && nodes.Add(item.NodeId) && (latest is null || item.CreatedAt >= latest));
                        items.Add(item.Id, item);
                        latest = item.CreatedAt;
                    }
                }
                if (!current.HasNext)
                {
                    Require(items.Count == current.Total);
                    return new(new(current.Repository, current.Target, current.Coordination,
                        new(items.Values.OrderBy(item => item.Id).ToImmutableArray(), page, observed, bytes, true), sample),
                        null, GitHubDelivery.Read, RequiredPermissions: response.RequiredPermissions);
                }
                Require(current.Items.Length > 0 && current.Cursor is { Length: > 0 and <= 2048 } next && cursors.Add(next));
                cursor = current.Cursor;
            }
            throw new GitHubProtocolException();
        }
        catch (GitHubProtocolException exception) { return GitHubApiResult<GitHubCampaignSnapshot>.Failed(exception.Code, dispatched: dispatched); }
        catch (OperationCanceledException)
        {
            return GitHubApiResult<GitHubCampaignSnapshot>.Failed(token.IsCancellationRequested
                ? CancellationFailure.Code : GitHubFailureCode.Timeout, dispatched: dispatched);
        }
        catch { return GitHubApiResult<GitHubCampaignSnapshot>.Failed(GitHubFailureCode.HostFailure, dispatched: dispatched); }

        GitHubApiResult<GitHubCampaignSnapshot> Failure<T>(GitHubApiResult<T> response) where T : class =>
            GitHubApiResult<GitHubCampaignSnapshot>.Failed(response.Failure?.Code == GitHubFailureCode.Cancelled
                && !token.IsCancellationRequested && deadline.IsCancellationRequested ? GitHubFailureCode.Timeout
                    : response.Failure?.Code ?? GitHubFailureCode.InvalidResponse,
                dispatched: dispatched, status: response.Failure?.HttpStatus, retry: response.Failure?.Retry,
                permissions: response.RequiredPermissions);
    }

    private CampaignPage ReadCampaignPage(JsonElement value)
    {
        Exact(value, "data");
        var data = Property(value, "data");
        Exact(data, "repository");
        var repository = Property(data, "repository");
        var identity = ObserveRepositoryIdentity(CampaignRepository(repository));
        var permission = NullableString(repository, "viewerPermission", 32);
        Require(permission is null or "READ" or "TRIAGE" or "TRIAGE_PLUS" or "WRITE" or "MAINTAIN" or "ADMIN");
        var facts = new GitHubCampaignRepositoryFacts(identity, Boolean(repository, "isPrivate"),
            Boolean(repository, "isArchived"), Boolean(repository, "isDisabled"), permission);
        var baseValue = Property(repository, "base");
        var target = BundleRef(baseValue, readAuthority.TargetRef) ?? throw new GitHubProtocolException();
        _ = Node(baseValue, "id");
        var coordValue = Property(repository, "coord");
        var coordination = BundleRef(coordValue, GitHubPublicationFactory.CreateCoordinationRef(readAuthority));
        if (coordination is not null) _ = Node(coordValue, "id");
        if (coordination is not null) ObserveCheckpointRefIdentity(coordination);
        var collection = Property(repository, "pullRequests");
        var nodes = Property(collection, "nodes");
        Require(nodes.ValueKind == JsonValueKind.Array && nodes.GetArrayLength() <= 100);
        var items = nodes.EnumerateArray().Select(item => CampaignPullRequest(item, identity)).ToImmutableArray();
        var page = Property(collection, "pageInfo");
        var hasNext = Boolean(page, "hasNextPage");
        var cursor = NullableString(page, "endCursor", 2048);
        Require(!hasNext || cursor is { Length: > 0 });
        return new(facts, target, coordination, items, (int)Integer(collection, "totalCount", 0, MaximumPullRequests),
            hasNext, cursor);
    }

    private static GitHubRepositoryIdentity CampaignRepository(JsonElement value)
    {
        var owner = CampaignActor(Property(value, "owner"));
        var name = String(value, "name", 256);
        Require(owner.Kind is GitHubActorKind.User or GitHubActorKind.Organization
            && RepositoryPart(owner.Login) && RepositoryPart(name));
        return new(BigDatabaseId(value, "fullDatabaseId"), Node(value, "id"), owner.Login, name);
    }

    private static GitHubActor CampaignActor(JsonElement value)
    {
        var login = String(value, "login", 256);
        Require(login.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '[' or ']'));
        var kind = String(value, "__typename", 32) switch
        {
            "User" => GitHubActorKind.User,
            "Bot" => GitHubActorKind.Bot,
            "Organization" => GitHubActorKind.Organization,
            "Mannequin" => GitHubActorKind.Mannequin,
            _ => throw new GitHubProtocolException()
        };
        return new(Positive(value, "databaseId"), Node(value, "id"), login, kind);
    }

    private static GitHubPullRequest CampaignPullRequest(JsonElement value, GitHubRepositoryIdentity identity)
    {
        var state = String(value, "state", 16);
        Require(state is "OPEN" or "CLOSED" or "MERGED");
        var merged = Boolean(value, "merged");
        var mergedAt = NullableDate(value, "mergedAt");
        var closedAt = NullableDate(value, "closedAt");
        Require((state == "OPEN") == (closedAt is null) && merged == (mergedAt is not null) && merged == (state == "MERGED"));
        var basis = CampaignRepository(Property(value, "baseRepository"));
        Require(basis == identity);
        var headRepository = Property(value, "headRepository");
        var head = headRepository.ValueKind == JsonValueKind.Null ? null : CampaignRepository(headRepository);
        var baseRef = String(value, "baseRefName", 1024);
        var headRef = String(value, "headRefName", 1024);
        var author = Property(value, "author");
        Require(RefName("refs/heads/" + baseRef) && RefName("refs/heads/" + headRef));
        return new(BigDatabaseId(value, "fullDatabaseId"), Node(value, "id"), (int)Integer(value, "number", 1, int.MaxValue),
            state == "OPEN", Boolean(value, "isDraft"), merged, mergedAt, closedAt, Date(value, "createdAt"),
            String(value, "title", 256), String(value, "body", 65536, allowEmpty: true),
            author.ValueKind == JsonValueKind.Null ? null : CampaignActor(author),
            new(head, headRef, Oid(value, "headRefOid")), basis, baseRef, Oid(value, "baseRefOid"), Boolean(value, "maintainerCanModify"));
    }

    private sealed record CampaignPage(GitHubCampaignRepositoryFacts Repository, GitHubRef Target, GitHubRef? Coordination,
        ImmutableArray<GitHubPullRequest> Items, int Total, bool HasNext, string? Cursor, int Bytes = 0);
}
