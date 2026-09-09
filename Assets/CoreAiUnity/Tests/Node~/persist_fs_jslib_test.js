'use strict';

// WHY: Node boundary proof for CoreAiPersistFs.jslib. The manual FS.syncfs channel is gone (Unity 6.3
// deprecated it and its callback never fired), so the jslib answers exactly one question, and it is the
// one C# cannot answer for itself: is the ENGINE'S automatic persistentDataPath persistence armed for
// this page? A wrong "yes" tells the player their data is durable while it dies with the tab; a wrong
// "no" turns a healthy build into a visible error. The REAL jslib is loaded from disk - nothing here
// re-implements it - and only the Emscripten `Module` object is a stand-in.

const fs = require('fs');
const path = require('path');

const unityRoot = path.join(__dirname, '..', '..');
const bridgePath = path.join(unityRoot, 'Runtime', 'Plugins', 'WebGL', 'CoreAiPersistFs.jslib');
const bridgeSource = fs.readFileSync(bridgePath, 'utf8');

// WHY: Emscripten evaluates the jslib with `Module` in scope; `new Function` reproduces that binding
// without pulling a whole emscripten runtime into the test.
function loadBridge(emscriptenModule) {
  const captured = {};
  const sandbox = {
    mergeInto: (target, library) => Object.assign(captured, library),
    LibraryManager: { library: {} },
    Module: emscriptenModule,
  };
  const factory = new Function(...Object.keys(sandbox), bridgeSource);
  factory(...Object.values(sandbox));
  return captured;
}

function autoSyncEnabled(emscriptenModule) {
  return loadBridge(emscriptenModule).CoreAi_PersistFsAutoSyncEnabled();
}

function mountedModule(autoPersist, rawConfig) {
  return {
    __unityIdbfsMount: { mount: { opts: { autoPersist: autoPersist } } },
    autoSyncPersistentDataPath: rawConfig,
  };
}

function collectExterns(directory, found) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      collectExterns(entryPath, found);
      continue;
    }

    if (!entry.name.endsWith('.cs')) continue;
    const declarations = fs.readFileSync(entryPath, 'utf8')
      .match(/extern\s+\w+\s+(CoreAi_PersistFs\w+)/g) || [];
    for (const declaration of declarations) {
      found.add(declaration.slice(declaration.lastIndexOf(' ') + 1));
    }
  }

  return found;
}

let failures = 0;

function check(name, condition, detail) {
  if (condition) {
    console.log('PASS ' + name);
    return;
  }

  failures++;
  console.error('FAIL ' + name + (detail ? ' -- ' + detail : ''));
}

function run() {
  {
    const armed = autoSyncEnabled(mountedModule(true, false));
    check(
      'an engine mount with autoPersist reports armed storage',
      armed === 1,
      String(armed));
  }

  {
    // WHY: opts.autoPersist is the flag the engine actually acts on. A page can pass the config value
    // and still end up with a mount that does not persist; believing the config there would claim a
    // durability the player does not have.
    const armed = autoSyncEnabled(mountedModule(false, true));
    check(
      'a mount without autoPersist outranks a raw config that claims otherwise',
      armed === 0,
      String(armed));
  }

  {
    const armed = autoSyncEnabled({ autoSyncPersistentDataPath: true });
    check(
      'the raw config answers when a host replaced the engine mount',
      armed === 1,
      String(armed));
  }

  {
    const armed = autoSyncEnabled({ autoSyncPersistentDataPath: false });
    check(
      'a host mount without the config reports storage that is not armed',
      armed === 0,
      String(armed));
  }

  {
    const armed = autoSyncEnabled({ __unityIdbfsMount: {}, autoSyncPersistentDataPath: true });
    check(
      'a mount that exposes no opts falls back to the raw config',
      armed === 1,
      String(armed));
  }

  {
    const armed = autoSyncEnabled({});
    check(
      'a module that states nothing reports storage that is not armed',
      armed === 0,
      String(armed));
  }

  {
    const armed = autoSyncEnabled(undefined);
    check(
      'a missing module reports not armed instead of throwing into the player',
      armed === 0,
      String(armed));
  }

  {
    const hostile = {};
    Object.defineProperty(hostile, '__unityIdbfsMount', {
      get: () => { throw new Error('host replaced the mount accessor'); },
    });
    const armed = autoSyncEnabled(hostile);
    check(
      'a throwing module accessor reports not armed instead of throwing into the player',
      armed === 0,
      String(armed));
  }

  {
    // WHY: an export nobody imports (or an import with no export) is exactly what this wave left
    // behind when the manual sync channel was deleted: the C# side fails only at link time in a
    // WebGL build, which is the most expensive place to find it.
    const exported = Object.keys(loadBridge({})).sort();
    const imported = Array.from(collectExterns(path.join(unityRoot, 'Runtime'), new Set())).sort();
    check(
      'every jslib export has a C# import and every C# import has an export',
      JSON.stringify(exported) === JSON.stringify(imported),
      'jslib: ' + JSON.stringify(exported) + ' vs C#: ' + JSON.stringify(imported));
  }

  if (failures > 0) {
    process.exitCode = 1;
  }
}

run();
