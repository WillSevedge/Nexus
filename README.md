# Nexus

Nexus connects running **Revit**, **AutoCAD** and **Civil 3D** sessions to a **hub** that runs in
the background (tray icon, starts with Windows). Each host runs a small agent that answers requests
over a named pipe; the hub finds every running agent, lists the open documents, runs *readers*
against them, lets you edit values and write them back, and exports the results.

Later phases: Excel import/export, two-way sync and the MCP server.

## Solution layout

| Project | Target | What it is |
|---|---|---|
| `src/Nexus.Contracts` | net10.0 | *Shared.Contracts*: message envelope, DTOs (host, document, reader, item, property), pipe framing. No dependencies. |
| `src/Nexus.Agent.Shared` | net10.0 | *Shared.Agent*: pipe server, discovery file, request dispatch, host-thread work queue, reader registry, logging. No NuGet dependencies (loaded inside the hosts). |
| `src/Nexus.Agent.Revit` | net10.0-windows | Revit add-in (`IExternalApplication`), ribbon status button, `ExternalEvent` request queue, readers. |
| `src/Nexus.Agent.Acad` | net10.0-windows | AutoCAD-family core agent (`IExtensionApplication`), autoloader bundle, generic "Properties palette" reader, layouts reader, module loader. Loads in every AutoCAD-based product. |
| `src/Nexus.Agent.Acad.Civil3D` | net10.0-windows | Civil 3D module. The only project that references the Civil 3D API. Loaded by the core only when Civil 3D is detected. |
| `src/Nexus.Hub.Core` | net10.0 | UI-free hub logic: discovery, pipe client, result flattening, CSV/JSON export. |
| `src/Nexus.Hub` | net10.0-windows (WPF) | The hub application (`Nexus.exe`). |
| `src/Nexus.Cli` | net10.0 | `nexus` command-line client, for testing agents without the GUI. |
| `tests/Nexus.Tests` | net10.0 | xUnit tests (no Autodesk product needed): pipe round trip with a fake host, threading, errors, busy-host handling, flattening, CSV. |

Build settings: `Directory.Build.props` (common), `build/HostVersions.props` (host version
matrix), `build/Revit.targets` and `build/AutoCad.targets` (API references and deployment).
Deployment templates are in `deploy/`.

## Prerequisites

- Windows, Visual Studio 2026 (includes the .NET 10 SDK). The 2026 hosts (Revit 2026 with API 26.5+, AutoCAD 2026, Civil 3D 2026) run on .NET 10, so every project targets .NET 10.
- Revit 2026 and/or AutoCAD 2026 / Civil 3D 2026 installed in the default folders
  (`C:\Program Files\Autodesk\Revit 2026\`, `C:\Program Files\Autodesk\AutoCAD 2026\`).

Autodesk API DLLs are referenced from those install folders with `Private=false`, so they
are never copied or redistributed. If a product is **not** installed on the build machine (e.g. CI),
the build falls back to compile-only reference packages (`Nice3point.Revit.Api.*`,
`AutoCAD.NET`, `Civil3D.NET`) with `ExcludeAssets=runtime`. These are never copied either. The build
prints which one it used. To point at a different install folder, pass
`/p:RevitInstallDir=...\` or `/p:AutoCadInstallDir=...\`.

## Build and deploy

Close Revit, AutoCAD and Civil 3D first: they lock the DLLs. (If one is open, the build
still succeeds but prints copy warnings and the old version stays deployed.)

Visual Studio: open `Nexus.sln`, choose **Debug**, **Build > Build Solution**.

Command line:

```powershell
dotnet build Nexus.sln -c Debug
dotnet test tests\Nexus.Tests
```

On Windows, every build deploys automatically:

| What | Where |
|---|---|
| Revit manifest | `%APPDATA%\Autodesk\Revit\Addins\2026\Nexus.addin` |
| Revit add-in files | `%APPDATA%\Autodesk\Revit\Addins\2026\Nexus\` |
| AutoCAD bundle manifest | `%APPDATA%\Autodesk\ApplicationPlugins\Nexus.bundle\PackageContents.xml` |
| AutoCAD core + Civil 3D module | `%APPDATA%\Autodesk\ApplicationPlugins\Nexus.bundle\Contents\2026\` |
| Hub | `src\Nexus.Hub\bin\Debug\net10.0-windows\Nexus.exe` (not deployed; run it from there) |

Add `/p:DeployToHost=false` to build without deploying. `dotnet clean` removes the deployed files.

Runtime files (all under `%LOCALAPPDATA%\Nexus\`):

| Path | Contents |
|---|---|
| `agents\{pid}.json` | One registration per running agent (how the hub finds them). |
| `logs\revit-*.log`, `logs\acad-*.log`, `logs\hub-*.log` | Logs. Every error is logged here with a stack trace. |
| `exports\` | Default export folder. |
| `acad-probe-*.txt` | AutoCAD crash guard for the generic property reader (see below). |

## Revit 2026

**Load**
1. Build (see above), then start Revit 2026.
2. Revit asks about the unsigned add-in "Nexus Agent": choose **Always Load**.
3. A **Nexus** ribbon tab appears with a **Hub Link** panel. The **Hub** button shows the connection state:
   - grey **Hub: Starting** until Revit is idle,
   - blue **Hub: Waiting** while listening for the hub,
   - green **Hub: Connected** while the hub is connected,
   - red **Hub: Error**.
   Click it for details (pipe name, requests handled, last error), **Show the Nexus hub**, or the log folder.

**Readers**
- `revit.sheets`: one item per sheet (placeholders optional).
  - Every sheet parameter (built-in, project and shared), grouped like the Properties palette (`Sheet · Identity Data`, …), in palette order, then hidden parameters.
  - Title block instance parameters (`Title Block (Instance) · …`) and type parameters (`Title Block (Type) · …`). If a sheet has more than one title block, each is listed separately and a warning is added.
  - `Current Revision`: number on the sheet, date, description, sequence, issued, issued by/to, plus the sheet's revision history.
  - `Element`: element id, unique id, workset, and whether the element can be edited.
- `revit.projectinfo`: every Project Information parameter.
- Placeholders (registered, return `NotImplemented`): `revit.elements`, `revit.schedules`, `revit.views`, `revit.rooms`, `revit.mepsystems`.

Every parameter carries:
- source (BuiltIn / Project / Shared / Family) and id (BuiltInParameter name, shared GUID, or parameter element id),
- storage type, spec (data type) and display units,
- display value and raw value (internal units),
- read-only flag and reason: read-only parameter, not user-modifiable, document read-only or linked, element borrowed by another user, changed in central, or workset owned by someone else.

**Editing.** The agent accepts edits from the hub (see *Hub › Editing*). All edits to one
document go into one transaction, `Nexus: edit N values`, so one **Undo** in Revit reverts the
batch. Per value:
- text: set as typed; whole numbers; Yes/No accepts yes/no, true/false, 1/0;
- numbers with units (lengths, areas, angles…): typed in project units, e.g. `10' 6"` or `3200`;
- values that refer to other elements (materials, types, levels…): not editable yet.

A value is skipped when the element is not editable (see the read-only reasons above) or when it
has changed in Revit since it was read. If Revit reports an error when committing, the whole batch
is rolled back. Revit warnings (e.g. a duplicate mark) are passed back to the hub instead of
showing a dialog. Editing a title block **type** parameter changes every sheet that uses that type.

**Threading.** Pipe requests never touch the Revit API. They are queued and run by an
`IExternalEventHandler` on Revit's main thread. If Revit cannot run them within 30 s
(for example a modal dialog is open or you are in an edit mode), the hub gets a `HostBusy`
error instead of waiting forever.

**Debug**
- Set `Nexus.Agent.Revit` as the startup project and press F5. The "Revit 2026" launch profile starts Revit.
- Or attach to a running Revit: **Debug > Attach to Process…**, pick `Revit.exe`, code type **Managed (.NET Core, .NET 5+)**.

## AutoCAD 2026 / Civil 3D 2026

**Load**
1. Build, then start AutoCAD 2026 or Civil 3D 2026.
2. The bundle in `%APPDATA%\Autodesk\ApplicationPlugins` loads automatically.
   - AutoCAD asks about loading `Nexus.Agent.Acad.dll` from a non-trusted location: choose **Always Load**.
   - Or add `%APPDATA%\Autodesk\ApplicationPlugins\Nexus.bundle\...` to `TRUSTEDPATHS`.
3. A **Nexus** ribbon tab appears with a **Hub Link** panel. Its **Hub** button shows the connection
   state with the same colors as in Revit. Click it (or type `NEXUS`) for details and **Show the Nexus hub**.
   The tab comes back after a workspace switch. `NEXUSSTATUS` prints the same details on the command line.

In Civil 3D the status shows `Modules: Civil3D`, and the hub lists the host as **Civil 3D 2026**.

The core detects Civil 3D after startup, on the first idle. It looks for any of:
- `/product C3D` on the command line,
- a loaded `AeccDbMgd` assembly,
- a loaded `aecc*` ObjectARX module.

The detection rules are in `modules.json` next to the core DLL.

**Readers**
- `acad.layouts`: one item per layout, in tab order, plus a `Drawing` item.
  - Drawing item: drawing settings and, for the active drawing, the system variables the palette shows with nothing selected. These cover current layer, color, linetype, lineweight, annotation scale, UCS, plot style, view center/size and similar settings.
  - Each layout: page setup, plotter, paper size, plot style table, plot settings including shade plot and DPI, all layout COM/.NET properties, and the paper space view (view center/height/width, visual style, UCS settings, annotation scale).
  - Children: every paper space object with all its properties, grouped by the palette's own categories (General, Geometry, Text, Misc, …). This covers title blocks, text/mtext, tables, dimensions, viewports and Civil 3D labels and tables.
    - Block references also get **Attributes** (tag = value, constant ones read-only) and **Custom** (dynamic block properties).
    - Viewports get scale and VP-frozen layers.
    - Tables get their cell text.
  - Option `modelThroughViewports` (off by default) adds, under each plan viewport, the model space objects whose extents fall inside that viewport's view. Objects on frozen, off and VP-frozen layers are skipped. Limits: `maxModelObjectsPerViewport`, `maxPaperSpaceObjects`.
- `civil3d.objects` (Civil 3D only):
  - alignments, with their profiles as children,
  - surfaces, with statistics from `GetGeneralProperties`/`GetTinProperties`/…,
  - pipe networks, with pipes and structures as children,
  - corridors.

**How the generic property reader works** (`src/Nexus.Agent.Acad/PropertyEngine`)
1. **COM/ActiveX properties.** It enumerates the object's `ITypeInfo`, the same properties the Properties palette shows. Each property is put in the palette's own category by asking the object's `ICategorizeProperties` interface.
2. **.NET API properties.** It reflects the managed object's public properties. Any the COM layer did not already show go into `.NET · <Type>` groups.
3. **Fallback categories.** Properties without a category use `property-categories.json`, which you can edit.
4. **Read-only flags.** Each property is marked read-only if it has no setter, if its layer is locked, or if the drawing is read-only.

Properties that other plug-ins add to the palette through native code are not reachable from .NET.

**Crash guard.** A few native-backed getters can crash a host in a way .NET cannot catch.
The first time each property is read, its name is written to `acad-probe-pending.txt`.
If AutoCAD dies during a read, the next start moves that property to
`acad-probe-denied.txt` and never reads it again. The first read of a new object type is
slightly slower because of this; later reads are not.

**Editing.** The agent accepts edits from the hub (see *Hub › Editing*):
- block attributes (constant ones excepted), table cells, dynamic block properties (checked against the allowed values);
- Properties palette (COM) properties and .NET API properties with a setter: text, numbers, Yes/No, choices (enums),
  points as `x, y, z`, and colors (`ByLayer`, `ByBlock`, `1`-`255`, `Red`…, or `R,G,B`);
- drawing settings, and system variables of the active drawing.

Values that refer to other objects (layer/style ids, COM objects) are not editable yet; the `Layer`
property (text) is. A value is skipped when its layer is locked, the drawing is read-only, or it changed in
the drawing since it was read. Edits run with the document locked from the application context, so they are
recorded in AutoCAD's undo history (`U`).

**Threading.** Requests are marshaled to AutoCAD's main thread and run only in the
application context, never while a command is running. If a command stays active for more
than 30 s the hub gets `HostBusy`. Reads lock the document and use read-only transactions.
The agent handles "no active document" and reads non-active drawings too.

**Debug**
- Set `Nexus.Agent.Acad` as the startup project. Pick the launch profile "AutoCAD 2026" or "Civil 3D 2026 (Imperial)" and press F5.
- Or attach to a running `acad.exe` (code type **Managed (.NET Core, .NET 5+)**).
- To debug the Civil 3D module, attach to or launch Civil 3D. Its PDB is deployed next to it.

## Hub

Nexus runs in the background, like Autodesk Access: one per Windows user, with an **N** icon in the
notification area (next to the clock), and it starts when you sign in to Windows.

- **Install.** Building `Nexus.Hub` installs it to `%LOCALAPPDATA%\Nexus\Hub` and restarts it there in the
  background (the running hub is asked to exit first, so the files can be replaced). Set
  `/p:DeployToHost=false` to skip.
- **Open it.** Click the tray icon, or **Hub → Show the Nexus hub** in Revit/AutoCAD/Civil 3D, or run `Nexus.exe` again.
- **Close vs exit.** Closing the window hides it; Nexus keeps running. Right-click the tray icon →
  **Exit Nexus** to stop it.
- **Start with Windows.** On by default; toggle it in the tray menu (a per-user `Run` entry that starts
  `%LOCALAPPDATA%\Nexus\Hub\Nexus.exe --background`).
- **Hosts.** Revit, AutoCAD and Civil 3D sessions are picked up within a few seconds of starting or closing,
  even while the window is hidden. **Refresh hosts** also re-lists the open documents.
- **Debugging.** F5 on `Nexus.Hub` uses the `--replace` launch profile: the background hub exits and the
  debugger's copy takes over. Other switches: `--background`, `--shutdown`, `--install`.

1. **Hosts and open documents.** Every running agent, with product, year, modules and pid, and its open documents flagged active, read-only, workshared, linked or family. The active document of each host is pre-checked. Click **Refresh hosts** after opening or closing files or hosts.
2. **Readers.** Readers from all agents. Expand one to change its options. Placeholders are greyed out and return `NotImplemented`.
3. **Run readers.** Runs every checked reader on every checked document of the matching host type. Different hosts run in parallel.
4. **Results.** One entry per document × reader, with item count, time, warnings or error. Select an entry to see its warnings. Untick it to leave it out of the table and exports.
5. **Table.** One row per item (children indented), one column per checked property.
6. **Items.** Browse the selected result's item tree. Select an item to see every property with value, read-only flag and reason, source, storage type, data type, units and id.
7. **Columns.** Choose which groups and properties go into the table and exports. You can filter by name.
8. **Editing** (Revit, AutoCAD, Civil 3D). Double-click a value in the **Table** tab (or select it and start typing) to change it. Paper space objects, title block attributes and other child items are rows in the table too. The **Items** tab is for browsing.
   - Edited cells turn yellow; the tooltip shows the old value.
   - Grey cells cannot be edited; the tooltip says why (read-only parameter, borrowed element, host without editing, …).
   - **Apply N change(s)…** shows every edit (document, item, property, old → new). **Apply to the model** writes them and shows each outcome: *Applied*, *Unchanged*, *Skipped* (with why) or *Failed* (with why). Closing the window re-reads the changed results so the table shows the model's values.
   - **Discard changes** puts the table back. Re-running readers or rebuilding the table asks before discarding unapplied edits.
9. **Export.**
   - *CSV one row per item*: the table as shown, all rows.
   - *CSV one row per property*: includes read-only flags, sources and units.
   - *JSON*: the raw results.
   CSV files open directly in Excel.

### CLI

```powershell
cd src\Nexus.Cli\bin\Debug\net10.0
.\nexus agents
.\nexus docs <pid>
.\nexus readers <pid>
.\nexus read <pid> revit.sheets --csv sheets.csv
.\nexus read <pid> acad.layouts --opt modelThroughViewports=true --long layouts.csv --json layouts.json
```

## Troubleshooting

- **The hub shows no hosts.** Check the host loaded the add-in (Revit and AutoCAD: Nexus tab, or `NEXUSSTATUS`), check that Nexus is running (tray icon), and check `%LOCALAPPDATA%\Nexus\agents\`. The hub and the hosts must run as the same Windows user, and both elevated or both not elevated.
- **`HostBusy`.** Close dialogs, finish the active command or edit mode, then run again.
- **An error in the hub.** The full stack trace is in the host's log (`%LOCALAPPDATA%\Nexus\logs`).
- **A property disappeared from AutoCAD results.** Check `acad-probe-denied.txt`. Delete the line (or the file) to try that property again.

## Extending

**Add a reader.**
1. Implement `IHostDataReader<Document>`: a `ReaderDescriptor` with id, options and description, and a `Read(document, context)` method that adds `DataItem`s.
2. Register it in `RevitReaders.CreateRegistry()` or `AgentExtension.Initialize()`.

The hub, IPC and export code do not change.

**Add an AutoCAD vertical (e.g. Map 3D).**
1. Create `Nexus.Agent.Acad.<Vertical>` like the Civil 3D project and implement `IAcadAgentModule`.
2. Add an entry with detection rules to `modules.json`.

The core never references the vertical's API.

**Add a host version (e.g. 2027).**
1. Add a row to `build/HostVersions.props`. A commented 2027 row is there; set its API package versions and AutoCAD R-number (it stays on `net10.0-windows`).
2. Add `Debug.2027;Release.2027` to `Directory.Build.props` and to the solution configurations.
3. Add a `<Components>` block for R26.0 in `deploy/autocad/PackageContents.xml`.

Build with `-c Debug.2027`. Each year deploys to its own folder (`Addins\2027`, `Contents\2027`).

## Security and data

- `samples/` and `*.xlsm` are git-ignored. The budget workbooks contain client financial data and must never be committed.
- Named pipes are restricted to the current Windows user (`PipeOptions.CurrentUserOnly`).
