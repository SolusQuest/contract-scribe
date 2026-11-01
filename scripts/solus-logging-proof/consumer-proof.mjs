// Bounded manual registry-access proof. --dry-run performs no network operation.
import { readFileSync, writeFileSync, mkdirSync, copyFileSync, mkdtempSync, rmSync } from 'node:fs';
import { resolve, join, dirname } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
const own = dirname(fileURLToPath(import.meta.url));
const manifestPath = resolve(process.env.SOLUS_MANIFEST_PATH || join(own, 'logging-release.json'));
const manifest = JSON.parse(readFileSync(manifestPath));
const source = resolve(process.env.SOLUS_SOURCE_ROOT || resolve(own, '../../solus-source'));
const identities = {
  'SolusQuest/contract-scribe': '1297409952',
  'SolusQuest/agentic-pr-review': '1263978368',
};
const expected = process.argv[3];
const plan = {
  purpose:
    'cold registry acquisition under actual consumer GITHUB_TOKEN followed by unchanged local installed verifier and original CLI acquisition/identity handshake only',
  identities,
  source: manifest.source,
  packages: [
    ...manifest.packages.map((p) => ({ id: p.id, version: p.version, sha256: p.sha256 })),
    {
      id: manifest.agentApi.id,
      version: manifest.agentApi.version,
      sha256: manifest.agentApi.packageSha256,
    },
  ],
  payload: manifest.destinations.cli,
  execute: false,
  noStorage: true,
  noPackagePublication: true,
};
if (process.argv[2] !== '--execute') {
  if (process.argv[2] !== '--dry-run')
    throw new Error('use --dry-run or --execute EXPECTED_REPOSITORY');
  console.log(JSON.stringify(plan, null, 2));
} else {
  if (
    !Object.hasOwn(identities, expected) ||
    process.env.GITHUB_ACTIONS !== 'true' ||
    process.env.GITHUB_REF !== 'refs/heads/main' ||
    process.env.GITHUB_REPOSITORY !== expected ||
    process.env.GITHUB_REPOSITORY_ID !== identities[expected] ||
    !/^\d+$/.test(process.env.GITHUB_RUN_ID || '') ||
    process.env.RUNNER_OS !== 'Linux' ||
    process.env.RUNNER_ARCH !== 'X64'
  )
    throw new Error('consumer_context');
  const token = process.env.GITHUB_TOKEN;
  const actor = process.env.GITHUB_ACTOR;
  const bearer = process.env.SOLUS_CLI_READ_BEARER;
  if (!token || !bearer || !actor || !/^[-A-Za-z0-9\[\]]+$/.test(actor))
    throw new Error('existing_consumer_credentials_required');
  const load = (p) => import(pathToFileURL(join(source, p)));
  const { cleanEnvironment, selectedSource } = await load('scripts/logging/common.ts');
  if (JSON.stringify(selectedSource()) !== JSON.stringify(manifest.source))
    throw new Error('exact_source');
  const history = spawnSync(
    'git',
    ['cat-file', '-e', `${manifest.agentApi.sourceCommit}^{commit}`],
    { cwd: source, timeout: 5000, stdio: 'ignore' },
  );
  if (history.status !== 0 || history.error || history.signal)
    throw new Error('historical_api_source_required');
  const { downloadArtifact } = await load('actions/logging/transport.mjs');
  const { inspectArchive, extractArchive } = await load('actions/logging/archive.mjs');
  const { runCli, childEnvironment } = await load('actions/logging/process.mjs');
  const { supportedHost } = await load('actions/logging/input.mjs');
  if (!(await supportedHost())) throw new Error('requires_Ubuntu24.04_x64');
  const workspace = mkdtempSync(join(tmpdir(), 'solus-123-registry-'));
  const hash = (p) => createHash('sha256').update(readFileSync(p)).digest('hex');
  try {
    const cache = join(workspace, 'packages');
    const home = join(workspace, 'home');
    mkdirSync(home);
    mkdirSync(cache);
    const env = cleanEnvironment(home);
    env.NUGET_PACKAGES = cache;
    env.NuGetPackageSourceCredentials_selected = `Username=${actor};Password=${token};ValidAuthenticationTypes=Basic`;
    const selected = plan.packages;
    writeFileSync(join(workspace, 'global.json'), readFileSync(join(source, 'global.json')));
    writeFileSync(
      join(workspace, 'NuGet.Config'),
      '<configuration><packageSources><clear/><add key="selected" value="https://nuget.pkg.github.com/SolusQuest/index.json"/></packageSources><packageSourceMapping><clear/><packageSource key="selected"><package pattern="Solus.*"/><package pattern="SolusAgent.*"/></packageSource></packageSourceMapping><fallbackPackageFolders><clear/></fallbackPackageFolders><disabledPackageSources><clear/></disabledPackageSources></configuration>\n',
    );
    writeFileSync(
      join(workspace, 'Fetch.csproj'),
      `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup>${selected.map((p) => `<PackageReference Include="${p.id}" Version="[${p.version}]"/>`).join('')}</ItemGroup></Project>\n`,
    );
    const restored = spawnSync(
      'dotnet',
      [
        'restore',
        'Fetch.csproj',
        '--configfile',
        'NuGet.Config',
        '--no-http-cache',
        '-p:ImportDirectoryBuildProps=false',
        '-p:ImportDirectoryBuildTargets=false',
        '-p:ManagePackageVersionsCentrally=false',
        '-p:RestoreFallbackFolders=',
        `-p:RestorePackagesPath=${cache}`,
      ],
      { cwd: workspace, env, timeout: 180000, maxBuffer: 8 * 1024 ** 2 },
    );
    // Do not echo credential-bearing child environment or arbitrary server/restore output.
    delete env.NuGetPackageSourceCredentials_selected;
    if (restored.status !== 0 || restored.error || restored.signal)
      throw new Error('cold_registry_restore_failed');
    const acquired = join(workspace, 'acquired');
    mkdirSync(acquired);
    const feed = join(acquired, manifest.packageDirectory);
    mkdirSync(feed);
    const baseline = join(acquired, manifest.validationBaseline.directory);
    mkdirSync(baseline);
    const payload = join(acquired, manifest.payload.directory);
    mkdirSync(payload);
    const evidence = [];
    for (const p of selected) {
      const base = join(cache, p.id.toLowerCase(), p.version);
      const original = join(base, `${p.id.toLowerCase()}.${p.version}.nupkg`);
      const metadata = JSON.parse(readFileSync(join(base, '.nupkg.metadata')));
      if (
        metadata.source.replace(/\/$/, '') !==
          'https://nuget.pkg.github.com/SolusQuest/index.json' ||
        hash(original) !== p.sha256
      )
        throw new Error('registry_package_bytes_or_source');
      const target = join(
        p.id === manifest.agentApi.id ? baseline : feed,
        `${p.id}.${p.version}.nupkg`,
      );
      copyFileSync(original, target);
      evidence.push({
        id: p.id,
        version: p.version,
        sha256: hash(original),
        source: metadata.source,
        freshCache: true,
      });
    }
    const archive = await downloadArtifact(
      manifest.destinations.cli.blobUrl,
      bearer,
      AbortSignal.timeout(45000),
    );
    const verified = inspectArchive(archive, manifest.payload.sha256, manifest.version);
    writeFileSync(join(payload, manifest.payload.filename), archive);
    // Metadata is the reviewed original manifest/config, never regenerated from registry substitutions.
    for (const [name, expectedHash] of [
      ['oci-config.json', manifest.payload.ociConfigSha256],
      ['oci-manifest.json', manifest.payload.ociManifestSha256],
    ]) {
      const raw = Buffer.from(
        JSON.stringify(
          JSON.parse(readFileSync(join(dirname(manifestPath), manifest.payload.directory, name))),
        ) + '\n',
      );
      if (createHash('sha256').update(raw).digest('hex') !== expectedHash)
        throw new Error('original_oci_metadata_bytes');
      writeFileSync(join(payload, name), raw, { flag: 'wx' });
    }
    copyFileSync(manifestPath, join(acquired, 'logging-release.json'));
    const extracted = join(workspace, 'cli');
    mkdirSync(extracted);
    const executable = await extractArchive(verified, extracted);
    const identity = await runCli(
      executable,
      ['--version'],
      Buffer.alloc(0),
      childEnvironment({}),
      5000,
      AbortSignal.timeout(10000),
    );
    let value;
    try {
      value = JSON.parse(identity.output);
    } catch {
      throw new Error('cli_identity');
    }
    if (
      identity.code !== 0 ||
      identity.stderr !== 0 ||
      identity.signal ||
      identity.error ||
      Object.keys(value).sort().join(',') !== 'protocol,runtimeIdentifier,version' ||
      value.protocol !== 1 ||
      value.version !== manifest.version ||
      value.runtimeIdentifier !== 'linux-x64'
    )
      throw new Error('cli_identity');
    const { verifyConsumers } = await load('scripts/logging/verify-consumers.ts');
    const local = await verifyConsumers({
      feed,
      manifest: join(acquired, 'logging-release.json'),
      scenario: 'all',
      apiPackage: join(baseline, manifest.validationBaseline.file),
      packagesOnly: true,
    });
    // This outer evidence owns registry acquisition; the existing verifier correctly retains registryAccessVerified:false.
    const result = {
      repository: expected,
      repositoryId: identities[expected],
      runId: process.env.GITHUB_RUN_ID,
      runAttempt: process.env.GITHUB_RUN_ATTEMPT,
      workflowSha: process.env.GITHUB_WORKFLOW_SHA,
      source: manifest.source,
      candidateSha256: manifest.candidateSha256,
      registryAcquisition: evidence,
      cli: {
        sha256: manifest.payload.sha256,
        url: manifest.destinations.cli.blobUrl,
        identity: value,
      },
      installedLocalVerifier: local,
      noStorage: true,
      noPublication: true,
    };
    if (!process.env.SOLUS_PROOF_REPORT) throw new Error('report_path_required');
    writeFileSync(resolve(process.env.SOLUS_PROOF_REPORT), JSON.stringify(result, null, 2) + '\n', {
      flag: 'wx',
    });
    console.log(
      'Consumer registry acquisition, exact bytes, installed consumers and CLI identity passed; no storage invoked.',
    );
  } finally {
    rmSync(workspace, { recursive: true, force: true });
  }
}
