"""Bounded gates for the one issue-166 platform proof; no mutation or credentials."""
import datetime as dt
import hashlib
import io
import json
import re
import stat
import zipfile

import yaml

REPOSITORY = 'SolusQuest/contract-scribe-sandbox'
REPOSITORY_ID = 1361354906
REPOSITORY_NODE = 'R_kgDOUSSgmg'
PRODUCT = '22142cf04efc66b728e5cdb8951efa2d15934aca'
BOOTSTRAP = 'b4fdbb719974f6182cde137e5c900f91435424e7'
WORKFLOW = '.github/workflows/m5-github-proposal-proof.yml'
OBSERVER = '.github/workflows/pr-observer.yml'
OBSERVER_ID = 353111702
OBSERVER_DIGEST = '6dd062f5b64b7ebfe701b1d168e7d59e29610c7aafd39474023a67859a110c8b'
CRON = '17 * * * *'
ACTOR = {'id': 41898282, 'node_id': 'MDM6Qm90NDE4OTgyODI=', 'login': 'github-actions[bot]', 'type': 'Bot'}
READ_PERMISSIONS = {'contents': 'read', 'actions': 'read', 'pull-requests': 'read'}
WRITE_PERMISSIONS = {'contents': 'write', 'pull-requests': 'write'}
MAX_ARCHIVE = 1048576
MAX_CHECKPOINT = 524288
# Failed manual4 claim and manual5 publication, read back 2026-09-09T14:43:07Z.
# Frozen with reviewed source; never learn history from the target during a trial.
RETAINED_HISTORY = {
    'refs': [
        {'ref': 'refs/heads/contract-scribe/coordination/859d9029d66cf17166602bc07f5b0f4172e68d763cf8589a796706ac4d1588f5',
         'oid': 'ddbd8e6d89d5d5d88bbd3c4ca55a55d16268f0a4'},
        {'ref': 'refs/heads/contract-scribe/coordination/e7a6289b5748fc64638a8011d4bdadd3cfdda6b4279702b4b8f22932a892b3d0',
         'oid': 'ab87ad83cdc95223f74294a6686d1917c9c6d298'},
        {'ref': 'refs/heads/contract-scribe/proposals/29090711e7ec9dbde8311cdb3142d1949c298d139b140faded09cbdce8b9d5b5/d30f4d3006ee2dbbb1a9587dfa7649347b32ed295a0883422ac9026fc7991272',
         'oid': '86398aa1f7f4a2ca39f2c115dd0ef1962420dff0'}],
    'pulls': [{'number': 1, 'head': '86398aa1f7f4a2ca39f2c115dd0ef1962420dff0', 'state': 'open', 'draft': True,
               'body_digest': '187d3278f9c134b39777d4faf5745411588203b80a26cd2ef5d391c98fa1d802', 'actor': 41898282}]}
FIELDS = {'product_sha', 'source_sha', 'workflow_sha', 'base_sha', 'workflow_digest',
          'workflow_id', 'activation', 'not_before', 'not_after', 'manual_run_number',
          'scheduled_run_number', 'actor_id', 'actor_login', 'owner', 'issue', 'scenarios'}


class ProofFailure(Exception):
    """Only a closed code may cross the public diagnostic boundary."""


def require(condition, code):
    if not condition:
        raise ProofFailure(code)


def unique(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'duplicate-property')
        result[key] = value
    return result


def parse_json(data):
    try:
        return json.loads(data, object_pairs_hook=unique,
                          parse_constant=lambda _: require(False, 'invalid-number'))
    except (ValueError, UnicodeError, TypeError, RecursionError):
        raise ProofFailure('invalid-json') from None


def sha(data):
    return hashlib.sha256(data).hexdigest()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=True).encode()


def timestamp(value):
    require(isinstance(value, str) and re.fullmatch(r'\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ', value), 'invalid-time')
    try:
        return dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
    except ValueError:
        raise ProofFailure('invalid-time') from None


def configuration(raw):
    require(isinstance(raw, str) and len(raw.encode()) <= 8192, 'configuration-bound')
    value = parse_json(raw)
    require(isinstance(value, dict) and set(value) == FIELDS, 'configuration-fields')
    for key in ('product_sha', 'source_sha', 'workflow_sha', 'base_sha'):
        require(isinstance(value[key], str) and re.fullmatch('[0-9a-f]{40}', value[key]), 'revision')
    require(re.fullmatch('[0-9a-f]{64}', str(value['workflow_digest'])), 'workflow-digest')
    require(re.fullmatch('[0-9a-f]{32}', str(value['activation'])), 'activation-id')
    for key in ('workflow_id', 'manual_run_number', 'scheduled_run_number', 'actor_id', 'issue'):
        require(type(value[key]) is int and value[key] > 0, 'configuration-integer')
    require(value['product_sha'] == PRODUCT and value['base_sha'] != BOOTSTRAP, 'product-or-base')
    require(value['scheduled_run_number'] == value['manual_run_number'] + 1, 'role-slots')
    require(value['actor_id'] == 16307884 and value['actor_login'] == value['owner'] == 'Yuee98'
            and value['issue'] == 166 and value['scenarios'] == ['stale', 'published', 'replayed'], 'authorization-scope')
    duration = timestamp(value['not_after']) - timestamp(value['not_before'])
    require(dt.timedelta(0) < duration <= dt.timedelta(hours=6), 'window-bound')
    return value


class WorkflowLoader(yaml.BaseLoader):
    pass


def mapping(loader, node):
    return unique((loader.construct_object(k), loader.construct_object(v)) for k, v in node.value)


WorkflowLoader.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, mapping)


def workflow_document(data):
    require(len(data) <= 65536, 'workflow-bound')
    try:
        document = yaml.load(data, Loader=WorkflowLoader)
    except yaml.YAMLError:
        raise ProofFailure('workflow-syntax') from None
    require(isinstance(document, dict), 'workflow-shape')
    return document


def validate_workflow(data, config):
    require(sha(data) == config['workflow_digest'], 'workflow-digest')
    document = workflow_document(data)
    require(document.get('permissions') == {}, 'workflow-permissions')
    jobs = document.get('jobs', {})
    require(jobs.get('product', {}).get('permissions') == WRITE_PERMISSIONS, 'product-permissions')
    for name in ('preflight', 'readback'):
        require(jobs.get(name, {}).get('permissions') == READ_PERMISSIONS, 'read-permissions')
    require(document.get('on', {}).get('schedule') == [{'cron': CRON}], 'workflow-cron')
    return document


def role_gate(config, context, repository, run, topics, now):
    require(context['repository'] == REPOSITORY and context['repository_id'] == REPOSITORY_ID, 'context-repository')
    require(repository.get('id') == REPOSITORY_ID and repository.get('node_id') == REPOSITORY_NODE
            and repository.get('full_name') == REPOSITORY and repository.get('default_branch') == 'main'
            and repository.get('private') is False and repository.get('archived') is False, 'repository-identity')
    require(context['event'] in ('workflow_dispatch', 'schedule') and context['ref'] == 'refs/heads/main', 'event-or-ref')
    require(context['event'] != 'schedule' or context['cron'] == CRON, 'schedule-cron')
    require(context['workflow_sha'] == config['workflow_sha'] and context['sha'] == config['base_sha'], 'executing-revision')
    require(run.get('id') == context['run_id'] and run.get('workflow_id') == config['workflow_id']
            and run.get('path', '').split('@')[0] == WORKFLOW and run.get('head_sha') == config['base_sha']
            and run.get('event') == context['event'] and run.get('repository', {}).get('id') == REPOSITORY_ID, 'run-identity')
    live_topics = [topic for topic in topics if topic.startswith('m5-h3-')]
    # The required inactive schedule need not fit consumed live slots or the old window.
    if not live_topics:
        return 'inactive'
    require(live_topics == ['m5-h3-' + config['activation']], 'activation-topic')
    require(context['attempt'] == run.get('run_attempt') == 1, 'run-attempt')
    for name in ('actor', 'triggering_actor'):
        actor = run.get(name, {})
        require(actor.get('id') == config['actor_id'] and actor.get('login') == config['actor_login'], 'run-actor')
    role = 'manual' if context['event'] == 'workflow_dispatch' else 'scheduled'
    require(context['run_number'] == run.get('run_number') == config[role + '_run_number'], 'run-slot')
    require(timestamp(config['not_before']) <= timestamp(run['created_at']) <= now < timestamp(config['not_after']), 'activation-window')
    return role


def artifact_name(config, relay=False):
    return 'm5-h3-' + config['activation'] + '-' + sha(canonical(config)) + ('-relay' if relay else '')


def archive_checkpoint(data, expected_archive_digest, expected_content_digest=None):
    require(isinstance(expected_archive_digest, str) and re.fullmatch('[0-9a-f]{64}', expected_archive_digest), 'missing-archive-digest')
    require(0 < len(data) <= MAX_ARCHIVE and sha(data) == expected_archive_digest, 'archive-digest')
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            entries = archive.infolist()
            require(len(entries) == 1, 'archive-entry-count')
            entry = entries[0]
            mode = entry.external_attr >> 16
            require(entry.filename == 'checkpoint.json' and not entry.is_dir() and not entry.flag_bits & 1
                    and stat.S_IFMT(mode) in (0, stat.S_IFREG) and 0 < entry.file_size <= MAX_CHECKPOINT,
                    'archive-entry')
            with archive.open(entry) as stream:
                content = stream.read(MAX_CHECKPOINT + 1)
            require(len(content) == entry.file_size <= MAX_CHECKPOINT, 'checkpoint-bound')
    except (zipfile.BadZipFile, RuntimeError, OSError, NotImplementedError):
        raise ProofFailure('invalid-archive') from None
    require(expected_content_digest is None or sha(content) == expected_content_digest, 'checkpoint-digest')
    require(isinstance(parse_json(content), dict), 'checkpoint-json')
    return content


def scenario_result(scenario, exit_code, data, product_sha=PRODUCT):
    result = parse_json(data)
    expected = {'negative': (4, 'stale', 'start'), 'positive': (0, 'published', 'start'), 'replay': (0, 'replayed', 'resume')}[scenario]
    require(isinstance(result, dict) and exit_code == expected[0] and result.get('terminalLayer') == 'publication'
            and result.get('outcome') == 'github-proposal.' + expected[1]
            and result.get('campaignOperation') == expected[2] and result.get('cliContractBaseline') == product_sha,
            'unexpected-scenario-result')
    return result


DIAGNOSTIC_CODES = {'InvalidRequest', 'Authentication', 'Permission', 'NotFound', 'Conflict', 'Validation',
                    'RateLimit', 'Cancelled', 'Timeout', 'ResponseLost', 'InvalidResponse', 'HostFailure'}
DIAGNOSTIC_FIELDS = {
    'boundary': {'Reconcile', 'Repository', 'CoordinationRead', 'CoordinationClaim', 'CoordinationRecord',
                 'CoordinationAdvanceStale', 'CoordinationAdvanceContent', 'CoordinationAdvanceRef',
                 'GitInspect', 'GitInspectPredecessor', 'GitPrepare', 'GitCreateContent', 'GitAdvanceRef',
                 'PullRequestPreflight', 'PullRequestObserve', 'PullRequestCreate', 'PullRequestRecover'},
    'owner': {'Reconciler', 'Transport', 'Coordination', 'GitData', 'PullRequests'},
    'coordinationFailure': {'InvalidInput', 'MissingPredecessor', 'DifferentOperation', 'StageConflict',
                            'TargetMoved', 'HumanChange', 'Conflict', 'ObjectMismatch', 'Bounds', 'Unresolved', 'Transport'},
    'proposalFailure': {'InvalidInput', 'Integrity', 'Bounds', 'Conflict', 'Unresolved', 'Transport'},
    'pullRequestOutcome': {'Absent', 'Appendable', 'HeldDraft', 'Ready', 'Merged', 'ClosedUnmerged',
                           'StaleDraft', 'Conflict', 'Unresolved', 'Failed'},
    'transportCode': DIAGNOSTIC_CODES,
    'transportHttpStatus': None,
    'delivery': {'NotDispatched', 'Read', 'NeedsReadback', 'Ambiguous'},
    'recoveryCode': DIAGNOSTIC_CODES,
    'recoveryHttpStatus': None,
    'objectKind': {'Blob', 'Tree', 'Commit'},
    'predicate': {'InvalidCorrelation', 'Cancelled', 'UnhandledException', 'RepositoryUnavailable', 'DifferentOperation',
                  'TargetMoved', 'SuccessorMismatch', 'AppendMismatch', 'TransitionMismatch', 'UnexpectedProposalRef',
                  'ClaimMismatch', 'CurrentMismatch', 'ClaimedRefPresent', 'StaleStage', 'UnexpectedStage',
                  'AppendCreateForbidden', 'CompletionHeadChanged', 'ObservationLimit', 'LifecycleOutcome',
                  'MissingOrUnexpectedComponent'},
}


def publication_diagnostic(value):
    if not isinstance(value, dict) or value.keys() != DIAGNOSTIC_FIELDS.keys():
        return None
    for key, allowed in DIAGNOSTIC_FIELDS.items():
        item = value[key]
        if item is None and key not in ('boundary', 'owner'):
            continue
        if allowed is None:
            if type(item) is not int or not 100 <= item <= 599:
                return None
        elif not isinstance(item, str) or item not in allowed:
            return None
    return {key: value[key] for key in DIAGNOSTIC_FIELDS}


def unexpected_result_summary(exit_code, data):
    """Report only closed public codes; never reproduce an unexpected result body."""
    summary = {'exitCode': exit_code if type(exit_code) is int and -128 <= exit_code <= 255 else None,
               'terminalLayer': None, 'outcome': None, 'campaignOperation': None,
               'baselineMatches': False, 'diagnosticCodes': [], 'omittedDiagnosticCount': 0,
               'publicationDiagnostic': None}
    if not isinstance(data, bytes) or len(data) > 8192:
        return summary
    try:
        value = parse_json(data)
    except ProofFailure:
        return summary
    if not isinstance(value, dict):
        return summary
    outcomes = {'local-invalid', 'host-failure', 'published', 'replayed', 'no-op', 'awaiting-review',
                'merged', 'closed-unmerged', 'stale-base-after-create', 'stale', 'human-change',
                'conflict', 'permission', 'rate-limit', 'cancelled', 'timeout'}
    known = {'github-proposal.' + name for name in outcomes | {'admitted', 'recovered-content-partial',
             'recovered-ref-partial', 'campaign-contract-error'}}
    known |= {'campaign.' + name for name in {'complete', 'no-work', 'provider-retryable', 'budget-exhausted',
              'attempt-ambiguous', 'invalid-configuration', 'state-missing', 'state-present', 'state-corrupt',
              'state-unsafe', 'state-conflict', 'lease-conflict', 'lease-unverifiable', 'unsupported-revision',
              'incompatible-snapshot', 'patch-stale', 'load-failure', 'target-terminal', 'provider-terminal',
              'proposal-invalid', 'patch-rejected', 'patch-host-failure', 'state-publication-failure',
              'host-contract-error', 'cancelled', 'timeout'}}
    for key, allowed in [('terminalLayer', {'usage', 'preflight', 'campaign', 'presentation', 'publication'}),
                         ('outcome', {'github-proposal.' + name for name in outcomes}),
                         ('campaignOperation', {'start', 'resume'})]:
        if isinstance(value.get(key), str) and value[key] in allowed:
            summary[key] = value[key]
    summary['baselineMatches'] = value.get('cliContractBaseline') == PRODUCT
    codes = value.get('diagnosticCodes')
    if isinstance(codes, list):
        summary['diagnosticCodes'] = [code for code in codes[:16] if isinstance(code, str) and code in known]
        summary['omittedDiagnosticCount'] = len(codes) - len(summary['diagnosticCodes'])
    failures = outcomes - {'published', 'replayed', 'no-op', 'awaiting-review', 'merged'}
    if (summary['terminalLayer'] == 'publication' and summary['exitCode'] not in (None, 0)
            and summary['outcome'] in {'github-proposal.' + name for name in failures}):
        summary['publicationDiagnostic'] = publication_diagnostic(value.get('publicationDiagnostic'))
    return summary


def permission_group(log, setup_start, setup_end):
    start, end = timestamp(setup_start), timestamp(setup_end)
    rows = []
    for line in log.splitlines():
        match = re.match(r'^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.\d+)?Z\s+(.*)$', line)
        # API step times have only second precision. Never accept output from a
        # later run step in the same second as Set up job completed.
        if match and match[2].startswith('##[group]Run '):
            break
        if match and start <= timestamp(match[1] + 'Z') <= end:
            rows.append(match[2])
    require(rows.count('##[group]GITHUB_TOKEN Permissions') == 1, 'permission-group')
    offset = rows.index('##[group]GITHUB_TOKEN Permissions') + 1
    require('##[endgroup]' in rows[offset:], 'permission-group')
    values = []
    for row in rows[offset:offset + rows[offset:].index('##[endgroup]')]:
        match = re.fullmatch(r'([A-Za-z]+): (read|write|none)', row.strip())
        require(match is not None, 'permission-row')
        values.append((match[1].lower(), match[2]))
    grants = {k: v for k, v in unique(values).items() if v != 'none'}
    require(grants == {'contents': 'write', 'pullrequests': 'write', 'metadata': 'read'}, 'issued-permissions')
    return grants


def observer_classification(runs, jobs_by_run, eligible, observation_complete):
    if not eligible:
        return 'inconclusive'
    if len(runs) > 1:
        return 'inconclusive'
    if not runs:
        return 'absent-after-proven-eligible' if observation_complete else 'inconclusive'
    run = runs[0]
    jobs = jobs_by_run.get(run['id'])
    if jobs is None:
        return 'inconclusive'
    # GitHub's approval mechanism gives action_required before any job starts.
    if run.get('conclusion') == 'action_required' and not jobs:
        return 'approval-gated'
    if any(job.get('started_at') and job.get('status') in ('in_progress', 'completed') for job in jobs):
        return 'triggered'
    return 'inconclusive'
