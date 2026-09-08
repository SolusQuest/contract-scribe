"""One-target H3 driver. GitHub access here is GET-only; publication belongs to the CLI."""
import base64
import datetime as dt
import hashlib
import os
from pathlib import Path
import shutil
import stat
import struct
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

from proof_contract import *

HERE = Path(__file__).resolve().parent
SOURCE = HERE.parents[3]


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


class Api:
    def __init__(self, read_token=None):
        self.read_token = read_token

    def request(self, path, limit=2097152, redirect=False):
        require(path.startswith('repos/' + REPOSITORY + '/'), 'api-target')
        headers = {'Accept': 'application/vnd.github+json', 'User-Agent': 'ContractScribe-issue166-proof',
                   'X-GitHub-Api-Version': '2026-03-10', 'Cache-Control': 'no-cache'}
        if self.read_token:
            headers['Authorization'] = 'Bearer ' + self.read_token
        request = urllib.request.Request('https://api.github.com/' + path, headers=headers, method='GET')
        try:
            response = urllib.request.build_opener(NoRedirect).open(request, timeout=25)
        except urllib.error.HTTPError as error:
            if redirect and error.code == 302:
                return self.blob(error.headers['Location'], limit)
            raise ProofFailure('github-read-unavailable') from None
        with response:
            data = response.read(limit + 1)
        require(len(data) <= limit, 'github-read-bound')
        return data

    @staticmethod
    def blob(location, limit):
        url = urllib.parse.urlsplit(location)
        require(url.scheme == 'https' and url.port in (None, 443) and not url.username and not url.fragment
                and url.hostname and (url.hostname.endswith('.blob.core.windows.net')
                     or url.hostname.endswith('.actions.githubusercontent.com')), 'artifact-origin')
        # No GitHub Authorization header is forwarded to the signed storage URL.
        with urllib.request.build_opener(NoRedirect).open(location, timeout=25) as response:
            data = response.read(limit + 1)
        require(len(data) <= limit, 'artifact-download-bound')
        return data

    def get(self, suffix):
        return parse_json(self.request('repos/' + REPOSITORY + '/' + suffix))

    def pages(self, suffix, key=None):
        result = []
        for page in range(1, 11):
            value = self.get(suffix + ('&' if '?' in suffix else '?') + 'per_page=100&page=' + str(page))
            rows = value[key] if key else value
            require(isinstance(rows, list), 'list-shape')
            result.extend(rows)
            if len(rows) < 100:
                return result
        raise ProofFailure('pagination-bound')

    def content(self, path, commit):
        value = self.get('contents/' + path + '?ref=' + commit)
        require(value.get('type') == 'file' and value.get('encoding') == 'base64'
                and 0 < value.get('size', 0) <= MAX_CHECKPOINT, 'content-shape')
        content = base64.b64decode(''.join(value['content'].split()), validate=True)
        require(len(content) == value['size'], 'content-bound')
        require(hashlib.sha1(b'blob ' + str(len(content)).encode() + b'\0' + content).hexdigest() == value['sha'], 'git-blob')
        return content


def context():
    value = parse_json(os.environ['M5_CONTEXT'])
    for key in ('repository_id', 'run_id', 'run_number', 'attempt'):
        require(isinstance(value[key], (str, int)) and not isinstance(value[key], bool)
                and re.fullmatch('[0-9]+', str(value[key])), 'context-integer')
        value[key] = int(value[key])
    return value


def current_config():
    return configuration(os.environ['M5_CONFIG'])


def work():
    path = Path(os.environ['RUNNER_TEMP']) / 'm5-h3'
    path.mkdir(mode=0o700, exist_ok=True)
    require(not path.is_symlink() and stat.S_IMODE(path.stat().st_mode) == 0o700, 'private-work-directory')
    return path


def write_output(name, value):
    require(re.fullmatch('[a-z_]+', name), 'output-name')
    rendered = value if isinstance(value, str) else canonical(value).decode()
    require('\n' not in rendered and '\r' not in rendered and len(rendered) <= 16384, 'output-bound')
    with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as output:
        output.write(name + '=' + rendered + '\n')


def fresh_gate(config, ctx, api, now=None):
    now = now or dt.datetime.now(dt.timezone.utc)
    repository = api.get('')
    run = api.get('actions/runs/' + str(ctx['run_id']))
    role = role_gate(config, ctx, repository, run, repository.get('topics', []), now)
    installed = api.content(WORKFLOW, config['workflow_sha'])
    validate_workflow(installed, config)
    require(installed == (SOURCE / WORKFLOW).read_bytes(), 'reviewed-workflow-bytes')
    target = api.get('git/ref/heads/main')
    require(target.get('object', {}).get('sha') == config['base_sha'], 'target-moved')
    observer = api.get('actions/workflows/' + str(OBSERVER_ID))
    require(observer.get('id') == OBSERVER_ID and observer.get('path') == OBSERVER
            and observer.get('state') == 'active', 'observer-identity')
    require(sha(api.content(OBSERVER, config['base_sha'])) == OBSERVER_DIGEST, 'observer-bytes')
    return role


def private_checkpoint(directory, content):
    require(0 < len(content) <= MAX_CHECKPOINT, 'checkpoint-bound')
    directory.mkdir(mode=0o700, parents=False, exist_ok=False)
    require(stat.S_IMODE(directory.stat().st_mode) == 0o700 and directory.stat().st_uid == os.geteuid(), 'private-directory')
    path = directory / 'checkpoint.json'
    with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600), 'wb') as output:
        output.write(content)
    require(path.stat().st_nlink == 1 and stat.S_IMODE(path.stat().st_mode) == 0o600, 'private-file')
    return path


def clean_environment():
    environment = dict(os.environ)
    for key in list(environment):
        if key.startswith('CONTRACTSCRIBE_') or key in ('GITHUB_TOKEN', 'GH_TOKEN', 'M5_READ_TOKEN',
                'DOTNET_STARTUP_HOOKS', 'DOTNET_ADDITIONAL_DEPS', 'DOTNET_SHARED_STORE', 'ACTIONS_RUNTIME_TOKEN'):
            environment.pop(key, None)
    environment.update({'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_GLOBAL': '/dev/null',
                        'GIT_TERMINAL_PROMPT': '0', 'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_NOLOGO': '1'})
    return environment


def command(args, *, cwd=None, environment=None, timeout=300):
    result = subprocess.run(args, cwd=cwd, env=environment or clean_environment(),
                            capture_output=True, timeout=timeout)
    require(result.returncode == 0, 'preparation-failed')
    return result.stdout


def checkout(repository, revision, destination):
    require(not destination.exists(), 'checkout-present')
    command(['git', 'init', '--quiet', str(destination)])
    command(['git', '-c', 'credential.helper=', 'fetch', '--quiet', '--depth=1',
             'https://github.com/' + repository + '.git', revision], cwd=destination)
    command(['git', 'checkout', '--quiet', '--detach', 'FETCH_HEAD'], cwd=destination)
    require(command(['git', 'rev-parse', 'HEAD'], cwd=destination).decode().strip() == revision, 'checkout-revision')


def digest_field(value):
    require(isinstance(value, str) and re.fullmatch('sha256:[0-9a-f]{64}', value), 'artifact-digest-missing')
    return value[7:]


def producer_artifact(config, api, now):
    runs = api.pages('actions/workflows/' + str(config['workflow_id']) + '/runs?event=workflow_dispatch', 'workflow_runs')
    matches = [run for run in runs if run.get('run_number') == config['manual_run_number']]
    require(len(matches) == 1, 'producer-count')
    producer = api.get('actions/runs/' + str(matches[0]['id']))
    producer_ctx = {'repository': REPOSITORY, 'repository_id': REPOSITORY_ID, 'event': 'workflow_dispatch',
                    'ref': 'refs/heads/main', 'cron': '', 'workflow_sha': config['workflow_sha'],
                    'sha': config['base_sha'], 'run_id': producer['id'], 'run_number': config['manual_run_number'], 'attempt': 1}
    require(role_gate(config, producer_ctx, api.get(''), producer, ['m5-h3-' + config['activation']], now) == 'manual', 'producer-identity')
    require(producer.get('status') == 'completed' and producer.get('conclusion') == 'success', 'producer-not-successful')
    artifacts = api.pages('actions/runs/' + str(producer['id']) + '/artifacts', 'artifacts')
    require(len(artifacts) == 1 and artifacts[0].get('name') == artifact_name(config), 'producer-artifact-count')
    artifact = api.get('actions/artifacts/' + str(artifacts[0]['id']))
    origin = artifact.get('workflow_run', {})
    require(artifact.get('name') == artifact_name(config) and artifact.get('expired') is False
            and 0 < artifact.get('size_in_bytes', 0) <= MAX_ARCHIVE
            and origin.get('id') == producer['id'] and origin.get('repository_id') == REPOSITORY_ID
            and origin.get('head_repository_id') == REPOSITORY_ID and origin.get('head_sha') == config['base_sha']
            and now < timestamp(artifact['expires_at']) and timestamp(config['not_after']) <= timestamp(artifact['expires_at']),
            'artifact-provenance')
    digest = digest_field(artifact.get('digest'))
    data = api.request('repos/' + REPOSITORY + '/actions/artifacts/' + str(artifact['id']) + '/zip', MAX_ARCHIVE, redirect=True)
    content = archive_checkpoint(data, digest)
    return content, {'producer_run': producer['id'], 'producer_attempt': 1, 'artifact_id': artifact['id'],
                     'archive_digest': digest, 'content_digest': sha(content), 'config_digest': sha(canonical(config)),
                     'receiver_run': context()['run_id'], 'receiver_attempt': 1}


def preflight():
    config, ctx = current_config(), context()
    api = Api(os.environ['M5_READ_TOKEN'])
    role = fresh_gate(config, ctx, api)
    write_output('role', role)
    write_output('artifact_name', artifact_name(config, relay=True))
    if role == 'scheduled':
        content, origin = producer_artifact(config, api, dt.datetime.now(dt.timezone.utc))
        private_checkpoint(work() / 'transfer', content)
        write_output('origin', origin)
    print('gate:' + role)


def write_configuration(config, directory, *, product_sha=None):
    campaign = parse_json((HERE / 'campaign.json').read_bytes())
    nonce = config['activation']
    campaign['planning']['campaignLineage'] = 'campaign.issue166.' + nonce
    campaign['planning']['productContractRevisionSha256'] = sha(('contract-scribe/campaign-product-revision/v1\0' + (product_sha or config['product_sha'])).encode())
    # CampaignConfiguration.Expect admits the product's declared field order.
    (directory / 'campaign.json').write_bytes(json.dumps(campaign, separators=(',', ':'), ensure_ascii=False).encode())
    publication = {'repositoryOwner': 'SolusQuest', 'repositoryName': 'contract-scribe-sandbox',
                   'targetRef': 'refs/heads/main', 'expectedBaseCommitOid': config['base_sha'],
                   'operationId': 'operation.issue166.' + nonce, 'generationId': 'generation.issue166.' + nonce,
                   'policy': {'maximumDocumentationBlocks': 1, 'maximumDistinctChangedFiles': 1, 'maximumCumulativePatchBytes': 4096},
                   'transition': 'initial'}
    (directory / 'github-positive.json').write_bytes(canonical(publication))
    publication['expectedBaseCommitOid'] = BOOTSTRAP
    (directory / 'github-negative.json').write_bytes(canonical(publication))


def check_checkpoint(path, checker):
    info = path.lstat()
    require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and info.st_uid == os.geteuid()
            and stat.S_IMODE(info.st_mode) == 0o600 and 0 < info.st_size <= MAX_CHECKPOINT, 'checkpoint-file')
    result = parse_json(command(['dotnet', str(checker), str(path)]))
    require(result.get('accepted') is True and result.get('sha256') == sha(path.read_bytes()), 'checkpoint-parser')
    return result


def prepare_target(target):
    for name in ('Synthetic.cs', 'Synthetic.csproj'):
        require((target / name).read_bytes() == (HERE / name).read_bytes(), 'fixture-bytes')
    shutil.copyfile(HERE / 'policy.json', target / 'policy.json')
    command(['dotnet', 'build', 'Synthetic.csproj', '-c', 'Debug', '--nologo', '-nodeReuse:false'], cwd=target)


def relay_inputs(config, ctx):
    origin = parse_json(os.environ['M5_ORIGIN'])
    fields = {'producer_run', 'producer_attempt', 'artifact_id', 'archive_digest', 'content_digest',
              'config_digest', 'receiver_run', 'receiver_attempt'}
    require(isinstance(origin, dict) and set(origin) == fields, 'relay-origin-fields')
    require(origin['config_digest'] == sha(canonical(config)) and origin['receiver_run'] == ctx['run_id']
            and origin['receiver_attempt'] == ctx['attempt'] == 1 and origin['producer_attempt'] == 1, 'relay-origin')
    for key in ('artifact_id', 'producer_run'):
        require(type(origin[key]) is int and origin[key] > 0, 'relay-origin-identity')
    for key in ('archive_digest', 'content_digest'):
        require(re.fullmatch('[0-9a-f]{64}', str(origin[key])), 'relay-origin-digest')
    require(re.fullmatch('[1-9][0-9]*', os.environ.get('M5_RELAY_ID', ''))
            and re.fullmatch('[0-9a-f]{64}', os.environ.get('M5_RELAY_DIGEST', '')), 'relay-identity')
    return origin


def prepare():
    config, ctx = current_config(), context()
    require(fresh_gate(config, ctx, Api()) in ('manual', 'scheduled'), 'inactive')
    root = work()
    product, target = root / 'product', root / 'target'
    checkout('SolusQuest/contract-scribe', config['product_sha'], product)
    checkout(REPOSITORY, config['base_sha'], target)
    command(['dotnet', 'build', 'src/ContractScribe.Cli/ContractScribe.Cli.csproj', '-c', 'Release', '--nologo'], cwd=product)
    cli = product / 'src/ContractScribe.Cli/bin/Release/net10.0/ContractScribe.Cli.dll'
    require(config['product_sha'] in command(['dotnet', str(cli), '--version']).decode(), 'built-product')
    # The loader's default design-time configuration is Debug, including protected generated inputs.
    prepare_target(target)
    helper = root / 'checker-source'
    shutil.copytree(HERE / 'CheckpointCheck', helper)
    command(['dotnet', 'build', str(helper / 'CheckpointCheck.csproj'), '-c', 'Release',
             '-p:ProductCorePath=' + str(cli.parent / 'ContractScribe.Core.dll'),
             '-p:ImportDirectoryBuildProps=false', '-p:ImportDirectoryBuildTargets=false'])
    write_configuration(config, root)
    if ctx['event'] == 'schedule':
        origin = relay_inputs(config, ctx)
        staging = Path(os.environ['RUNNER_TEMP']) / 'm5-relay-download'
        files = list(staging.iterdir())
        require(len(files) == 1 and files[0].is_file() and not files[0].is_symlink(), 'relay-download-shape')
        require(files[0].stat().st_size <= MAX_ARCHIVE, 'relay-download-bound')
        content = archive_checkpoint(files[0].read_bytes(), os.environ['M5_RELAY_DIGEST'], origin['content_digest'])
        checkpoint = private_checkpoint(root / 'positive', content)
        check_checkpoint(checkpoint, helper / 'bin/Release/net10.0/CheckpointCheck.dll')
    print('preparation:complete')


def identity_hash(domain, *values):
    rows = [('domain', 'contract-scribe/github-' + domain + '/v1')] + [('value', value) for value in values]
    digest = hashlib.sha256()
    for pair in rows:
        for value in pair:
            data = value.encode()
            digest.update(struct.pack('>I', len(data)) + data)
    return digest.hexdigest()


def remote_snapshot(api):
    refs = api.pages('git/matching-refs/heads/contract-scribe/')
    pulls = api.pages('pulls?state=all&base=main')
    owned = [pr for pr in pulls if pr.get('head', {}).get('ref', '').startswith('contract-scribe/')]
    return {'refs': sorted([{'ref': ref['ref'], 'oid': ref['object']['sha']} for ref in refs], key=lambda ref: ref['ref']),
            'pulls': sorted([{'number': pr['number'], 'head': pr['head']['sha'], 'state': pr['state'], 'draft': pr['draft'],
                              'body_digest': sha((pr.get('body') or '').encode()), 'actor': pr['user']['id']} for pr in owned], key=lambda pr: pr['number'])}


def published_facts(config, api, *, inspect_trees=False):
    snapshot = remote_snapshot(api)
    require(len(snapshot['refs']) == 2 and len(snapshot['pulls']) == 1, 'published-resource-count')
    lineage = 'campaign.issue166.' + config['activation']
    key = identity_hash('coordination-ref', 'solusquest', 'contract-scribe-sandbox', 'refs/heads/main', lineage)
    coordination = [ref for ref in snapshot['refs'] if ref['ref'] == 'refs/heads/contract-scribe/coordination/' + key]
    proposals = [ref for ref in snapshot['refs'] if ref['ref'].startswith('refs/heads/contract-scribe/proposals/')]
    require(len(coordination) == len(proposals) == 1, 'publication-refs')
    state = parse_json(api.content('.contract-scribe/coordination-state-v1.json', coordination[0]['oid']))
    require(state.get('stage') == 'published' and state.get('repositoryId') == REPOSITORY
            and state.get('targetRef') == 'refs/heads/main' and state.get('targetCommitOid') == config['base_sha']
            and state.get('operationId') == 'operation.issue166.' + config['activation']
            and state.get('generationId') == 'generation.issue166.' + config['activation']
            and state.get('transition') == 'initial' and state.get('expectedBaseOid') == state.get('observedBaseOid') == config['base_sha']
            and state.get('proposalRefOid') == state.get('proposalCommitOid') == state.get('contentCommitOid') == proposals[0]['oid'], 'coordination-state')
    pr = api.get('pulls/' + str(snapshot['pulls'][0]['number']))
    require(pr.get('number') == state.get('pullRequestNumber') and pr.get('state') == 'open' and pr.get('draft') is True
            and pr.get('merged') is False and all(pr.get('user', {}).get(k) == v for k, v in ACTOR.items())
            and pr.get('head', {}).get('sha') == proposals[0]['oid'] and 'refs/heads/' + pr['head']['ref'] == proposals[0]['ref']
            and pr.get('base', {}).get('sha') == config['base_sha'] and pr['base']['ref'] == 'main'
            and pr['head']['repo']['id'] == pr['base']['repo']['id'] == REPOSITORY_ID, 'draft-ownership')
    generation = proposals[0]['ref'].split('/')[-1]
    marker = '<!-- contract-scribe-publication-v1 ownership=sha256:' + state['pullRequestCreationOperationCommitmentSha256'] + ' -->\n'
    body = marker + 'campaign=sha256:' + key + '\ngeneration=sha256:' + generation + '\nsnapshot=sha256:' + state['snapshotCommitmentSha256']
    body += '\npolicy=sha256:' + state['policyCommitmentSha256'] + '\nheadRef=sha256:' + sha(proposals[0]['ref'].encode())
    body += '\nbaseRef=sha256:' + sha(b'refs/heads/main') + '\ncreationOperationId=sha256:' + state['operationCommitmentSha256'] + '\n'
    require(pr.get('body') == body and pr.get('title') == 'ContractScribe proposal ' + generation
            and sha(marker.encode()) == state.get('ownershipMarkerSha256'), 'publication-marker')
    commit = api.get('git/commits/' + proposals[0]['oid'])
    require(commit.get('sha') == proposals[0]['oid'] and [p['sha'] for p in commit['parents']] == [config['base_sha']]
            and commit['tree']['sha'] == state['proposalTreeOid'], 'proposal-commit')
    changes = api.pages('pulls/' + str(pr['number']) + '/files')
    require(len(changes) == 1 and changes[0].get('filename') == 'Synthetic.cs' and changes[0].get('status') == 'modified', 'candidate-diff')
    candidate = api.content('Synthetic.cs', proposals[0]['oid'])
    original = (HERE / 'Synthetic.cs').read_bytes()
    remove_docs = lambda data: b'\n'.join(line for line in data.split(b'\n') if not line.lstrip().startswith(b'///'))
    require(remove_docs(candidate) == remove_docs(original) and candidate != original, 'candidate-source')
    require(state.get('cumulativeChangedFiles') == [{'path': 'Synthetic.cs', 'candidateSha256': sha(candidate)}]
            and state.get('cumulativeDocumentationBlocks') == 1, 'candidate-state')
    if inspect_trees:
        # This richer inspection uses only the separate read job's token.
        # The product job's anonymous observations stay below its read quota.
        base = api.get('git/commits/' + config['base_sha'])
        trees = []
        for oid in (base['tree']['sha'], commit['tree']['sha']):
            tree = api.get('git/trees/' + oid + '?recursive=1')
            require(tree.get('sha') == oid and tree.get('truncated') is False, 'tree-bound')
            trees.append(unique((row['path'], (row['mode'], row['type'], row['sha'])) for row in tree['tree'] if row['type'] != 'tree'))
        before, after = trees
        changed = {path for path in before.keys() | after.keys() if before.get(path) != after.get(path)}
        candidate_oid = hashlib.sha1(b'blob ' + str(len(candidate)).encode() + b'\0' + candidate).hexdigest()
        require(base.get('sha') == config['base_sha'] and changed == {'Synthetic.cs'}
                and before['Synthetic.cs'][:2] == after['Synthetic.cs'][:2] == ('100644', 'blob')
                and after['Synthetic.cs'][2] == candidate_oid, 'candidate-tree')
        coordination_commit = api.get('git/commits/' + coordination[0]['oid'])
        require(coordination_commit.get('sha') == coordination[0]['oid']
                and [p['sha'] for p in coordination_commit['parents']] == [state['coordinationPredecessorOid']], 'coordination-commit')
    return {'snapshot': snapshot, 'coordination_oid': coordination[0]['oid'], 'proposal_oid': proposals[0]['oid'],
            'tree_oid': commit['tree']['sha'], 'pull_request': pr['number'], 'pull_request_url': pr['html_url'],
            'actor_id': ACTOR['id'], 'created_at': pr['created_at'], 'head_ref': pr['head']['ref']}


def invoke(scenario):
    config, ctx = current_config(), context()
    role = fresh_gate(config, ctx, Api())
    require((role == 'manual' and scenario in ('negative', 'positive')) or (role == 'scheduled' and scenario == 'replay'), 'scenario-role')
    root = work()
    api = Api()  # Never receives the product token.
    if scenario == 'negative':
        before = remote_snapshot(api)
        require(before == {'refs': [], 'pulls': []}, 'preexisting-work')
        (root / 'before.json').write_bytes(canonical(before))
    elif scenario == 'positive':
        require((root / 'negative.ok').read_text() == 'stale', 'negative-not-complete')
    else:
        before = published_facts(config, api)
        (root / 'before.json').write_bytes(canonical(before))
    directory = root / ('negative' if scenario == 'negative' else 'positive')
    if scenario != 'replay':
        directory.mkdir(mode=0o700, exist_ok=False)
    checkpoint = directory / 'checkpoint.json'
    provider_environment = clean_environment()
    provider = subprocess.Popen([sys.executable, str(HERE / 'provider.py')], env=provider_environment,
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        # Provider readiness is local and bounded; it never receives the product credential.
        from provider import wait_ready
        wait_ready(provider)
        # Re-read after preparation/readback so queued or delayed work cannot retain a stale gate.
        require(fresh_gate(config, ctx, Api()) == role, 'gate-changed')
        environment = clean_environment()
        credential = os.environ.get('CONTRACTSCRIBE_GITHUB_TOKEN')
        require(bool(credential), 'product-credential-absent')
        environment['CONTRACTSCRIBE_GITHUB_TOKEN'] = credential
        cli = root / 'product/src/ContractScribe.Cli/bin/Release/net10.0/ContractScribe.Cli.dll'
        args = ['dotnet', str(cli), 'github-proposal', 'resume' if scenario == 'replay' else 'start',
                '--repository-root', str(root / 'target'), '--input', 'Synthetic.csproj', '--policy', 'policy.json',
                '--snapshot', 'snapshot.issue166.' + config['activation'], '--state', str(checkpoint),
                '--configuration', str(root / 'campaign.json'), '--github-configuration',
                str(root / ('github-negative.json' if scenario == 'negative' else 'github-positive.json'))]
        process = subprocess.run(args, env=environment, capture_output=True, timeout=180)
        result = scenario_result(scenario, process.returncode, process.stdout)
        (root / (scenario + '-result.json')).write_bytes(canonical(result))
    finally:
        provider.terminate()
        try:
            provider.wait(timeout=5)
        except subprocess.TimeoutExpired:
            provider.kill()
            provider.wait()
    if scenario == 'negative':
        require(remote_snapshot(api) == before, 'negative-mutated-resources')
        (root / 'negative.ok').write_text('stale')
    else:
        facts = published_facts(config, api)
        if scenario == 'replay':
            require(facts == before, 'replay-mutated-resources')
        checker = root / 'checker-source/bin/Release/net10.0/CheckpointCheck.dll'
        accepted = check_checkpoint(checkpoint, checker)
        require(accepted['lineage'] == 'campaign.issue166.' + config['activation'], 'checkpoint-lineage')
        require((root / 'target/Synthetic.cs').read_bytes() == (HERE / 'Synthetic.cs').read_bytes(), 'local-source-mutated')
        (root / 'facts.json').write_bytes(canonical(facts))
    print('scenario:' + scenario + ':' + result['outcome'])


def outputs():
    config = current_config()
    facts = parse_json((work() / 'facts.json').read_bytes())
    write_output('facts', facts)
    write_output('artifact_name', artifact_name(config))


def readback():
    config, ctx = current_config(), context()
    api = Api(os.environ['M5_READ_TOKEN'])
    require(fresh_gate(config, ctx, api) in ('manual', 'scheduled'), 'inactive')
    facts = parse_json(os.environ['M5_FACTS'])
    require(published_facts(config, api, inspect_trees=True) == facts, 'final-resource-drift')
    jobs = api.pages('actions/runs/' + str(ctx['run_id']) + '/attempts/1/jobs', 'jobs')
    product = [job for job in jobs if job.get('name') == 'Product proof']
    require(len(product) == 1 and product[0].get('status') == 'completed' and product[0].get('conclusion') == 'success'
            and product[0].get('run_id') == ctx['run_id'], 'product-job-identity')
    setup = [step for step in product[0]['steps'] if step.get('name') == 'Set up job' and step.get('number') == 1]
    require(len(setup) == 1 and setup[0].get('conclusion') == 'success', 'setup-job-identity')
    log = api.request('repos/' + REPOSITORY + '/actions/jobs/' + str(product[0]['id']) + '/logs', 4194304, redirect=True).decode('utf-8')
    grants = permission_group(log, setup[0]['started_at'], setup[0]['completed_at'])
    deadline = time.monotonic() + 600
    classification = 'inconclusive'
    while True:
        candidates = api.pages('actions/workflows/' + str(OBSERVER_ID) + '/runs?event=pull_request', 'workflow_runs')
        runs = [run for run in candidates if any(pr.get('number') == facts['pull_request'] for pr in run.get('pull_requests', []))
                and timestamp(run['created_at']) >= timestamp(facts['created_at'])]
        job_map = {run['id']: api.pages('actions/runs/' + str(run['id']) + '/attempts/' + str(run['run_attempt']) + '/jobs', 'jobs') for run in runs}
        classification = observer_classification(runs, job_map, eligible=True, observation_complete=time.monotonic() >= deadline)
        if classification != 'inconclusive' or time.monotonic() >= deadline:
            break
        time.sleep(20)
    require(classification != 'inconclusive', 'observer-inconclusive')
    summary = {'issue': 166, 'run_id': ctx['run_id'], 'attempt': ctx['attempt'], 'product_job_id': product[0]['id'],
               'permissions': grants, 'observer': classification, 'facts': facts,
               'product_sha': config['product_sha'], 'source_sha': config['source_sha'], 'workflow_sha': config['workflow_sha'],
               'workflow_digest': config['workflow_digest'], 'base_sha': config['base_sha'], 'activation': config['activation']}
    with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as output:
        output.write('Issue 166 bounded run facts (not completion or live reauthorization):\n\n```json\n' + canonical(summary).decode() + '\n```\n')
    print('readback:verified:' + classification)


def main():
    try:
        action = sys.argv[1]
        if action == 'preflight':
            preflight()
        elif action == 'prepare':
            prepare()
        elif action == 'gate':
            require(fresh_gate(current_config(), context(), Api()) in ('manual', 'scheduled'), 'inactive')
        elif action == 'relay-gate':
            require(fresh_gate(current_config(), context(), Api()) == 'scheduled', 'relay-role')
            relay_inputs(current_config(), context())
        elif action == 'invoke':
            invoke(sys.argv[2])
        elif action == 'outputs':
            outputs()
        elif action == 'readback':
            readback()
        else:
            raise ProofFailure('unknown-command')
        return 0
    except ProofFailure as failure:
        print('proof-stopped:' + str(failure))
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError):
        print('proof-stopped:unavailable-or-invalid-input')
    return 1


if __name__ == '__main__':
    raise SystemExit(main())
