# HFSQL ODBC Studio

A Windows desktop tool (C# / .NET 8 / WPF, **64-bit only**) for testing and working with a **WinDev HFSQL Client/Server** database through ODBC.

Its main job is to show **which SQL statements, especially DDL** (`CREATE TABLE`, `ALTER TABLE`, `DROP TABLE`, `CREATE INDEX`), the HFSQL ODBC driver actually passes through to the HFSQL server. Driver errors are always shown **exactly as returned**: every `OdbcError` with its SQLSTATE, native error code, source and message, unmodified.

---

## Requirements

| What | Notes |
|---|---|
| Windows 10 / 11, **64-bit** | The app is an x64 process. |
| **64-bit HFSQL ODBC driver** (PC SOFT) | Must be installed on the machine. A 32-bit-only driver will **not** work. The app detects this case and warns: *"Only a 32-bit HFSQL ODBC driver is installed. This app is 64-bit and needs the 64-bit HFSQL ODBC driver."* |
| Network access to the HFSQL C/S server | Default port **4900**. |
| .NET 8 SDK | **Only to build.** The published EXE is self-contained, so no .NET install is needed to run it. |

Check the installed 64-bit drivers with `C:\Windows\System32\odbcad32.exe` → *Drivers* tab (this is the 64-bit ODBC administrator, despite the name).

---

## Build

```powershell
# Debug build (also works from Visual Studio 2022: open HfsqlOdbcStudio.sln, platform x64)
dotnet build HfsqlOdbcStudio.sln -c Debug -p:Platform=x64
```

The solution builds with **0 warnings**.

## Publish: one self-contained EXE

```powershell
.\build.ps1
```

This runs `dotnet publish -c Release -r win-x64` and copies the result to **`dist\HfsqlOdbcStudio.exe`**: a single EXE of about 75 MB with the .NET runtime embedded. Copy it to any 64-bit Windows machine that has the 64-bit HFSQL ODBC driver.

The `.csproj` sets `win-x64`, `x64`, `SelfContained`, `PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract`, `EnableCompressionInSingleFile` and `PublishReadyToRun`. Trimming is **off** because WPF does not support it.

---

## Using the app

### 1. Connect
Click **Connection…** in the top bar.

* **Server fields**: pick the ODBC driver, then enter server/host, port (4900), database, user and password. The driver list is read only from the 64-bit registry key `HKLM\SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers`, and any driver whose name contains "HFSQL" is pre-selected.
  The generated connection string is shown with the password masked. HFSQL keyword names can vary by driver version, so you can **Copy** it or click **Edit as connection string** to adjust it.
* **Existing DSN**: choose a 64-bit System or User DSN.
* **Connection string (advanced)**: a raw string, sent as-is. `%USER%` and `%PASSWORD%` are replaced with the credentials fields, so the password never has to be written into the string.

**Test Connection** shows the driver name, driver file and version, DBMS name/version and server version. On failure it shows the full ODBC error.

Profiles are saved to `%AppData%\HfsqlOdbcStudio\profiles.json`. Passwords (and raw connection strings) are encrypted with **DPAPI, CurrentUser scope**. The last used profile is offered as **Connect to "…"** at startup.

### 2. Explorer (left)
Lists user tables from `GetSchema("Tables")`, with search and a *Show system tables* toggle. Expand a table to see its columns (name, type, size, nullable). Right-click a table for: **Select top 100**, **Generate CREATE template**, **Generate ALTER template**, **Generate DROP**, **Copy name**.

### 3. Query workspace
* The **Template** dropdown inserts a sample statement: SELECT, INSERT, UPDATE, DELETE, CREATE TABLE, ALTER TABLE add/drop column, DROP TABLE, CREATE INDEX or Custom. If a table is selected in the explorer, its real table and column names are used. If the editor already holds your own SQL, you are asked before it is replaced.
* **Execute** with the button, **Ctrl+Enter** or **F5**. If text is selected, only the selection runs. Several statements separated by `;` run one after another, and execution stops at the first error.
* Row-returning statements use `ExecuteReader` and fill the grid, which can be sorted and copied. **Max rows** defaults to 1000; 0 means unlimited. All other statements use `ExecuteNonQuery` and report rows affected. Every run shows its execution time.
* **Cancel** stops a running statement. Queries run asynchronously, so the UI never freezes.
* You are asked to confirm before any `DROP`, any `DELETE` without `WHERE`, `ALTER … DROP`, or `TRUNCATE`.
* After a successful DDL statement, the app refreshes the explorer, **verifies the change through the schema** (for example *"Verified on server: table NewTable exists"*) and highlights the affected table.
* **Messages**: a timestamped log of every statement and its result or full ODBC error.
* **History**: the last 100 statements. Double-click one to load it into the editor.

### 4. Run the DDL capability test
1. Connect to the database.
2. Click **Run DDL Capability Test** (toolbar) or **Run test** on the *DDL Test* tab.
3. If `ZZ_ODBC_TEST` already exists, the app asks whether to drop it first.
4. The steps run in order. Each step is isolated, so a failure never stops the next one:
   1. `CREATE TABLE ZZ_ODBC_TEST (ID INTEGER, Name VARCHAR(50))`
   2. Verify the table exists (schema)
   3. `INSERT` a row, then `SELECT` it back
   4. `ALTER TABLE ZZ_ODBC_TEST ADD Extra VARCHAR(20)` (falls back to `ADD COLUMN …`), then verify the column
   5. `ALTER TABLE ZZ_ODBC_TEST DROP COLUMN Extra` (falls back to `DROP Extra`), then verify
   6. `CREATE INDEX IDX_ZZ_ODBC_TEST_NAME ON ZZ_ODBC_TEST (Name)`
   7. `DROP TABLE ZZ_ODBC_TEST`, then verify it is gone
5. The checklist shows PASSED/FAILED per step, with each attempted SQL and the driver's exact error.
6. **Export report** saves a `.md` or `.txt` file. It contains the date/time, machine, Windows user, driver name/file/version, DBMS and server version, database, the masked connection string, and each step's SQL and result. **The password is never included.**

---

## Files and logs

| Path | Content |
|---|---|
| `%AppData%\HfsqlOdbcStudio\profiles.json` | Connection profiles (secrets DPAPI-encrypted) |
| `%AppData%\HfsqlOdbcStudio\settings.json` | Window position/size, theme, max rows |
| `%AppData%\HfsqlOdbcStudio\history.json` | Last 100 statements |
| `%AppData%\HfsqlOdbcStudio\logs\app-YYYYMMDD.log` | Diagnostics and unhandled exceptions (no passwords) |

## Project layout

```
HfsqlOdbcStudio.sln
build.ps1                      publish → dist\HfsqlOdbcStudio.exe
src/HfsqlOdbcStudio/
  Assets/        app.ico, app.png, Sql.xshd (SQL highlighting)
  Models/        profiles, schema objects, query results, DDL test steps, settings
  Services/      OdbcDriverService, ConnectionService, SchemaService, QueryService,
                 ProfileService/SettingsService/HistoryService, DdlTestService,
                 UI services (dialogs, snackbar notifications, theme)
  Helpers/       SQL lexer/splitter, SqlAnalyzer, SqlTemplates,
                 ConnectionStringFactory, OdbcErrorFormatter, paths/logging/JSON
  ViewModels/    Main, Connection, Explorer, Query, DdlTest (CommunityToolkit.Mvvm)
  Views/         MainWindow, ConnectionWindow, ExplorerView, QueryView, DdlTestView
  Converters/
```

Tech: .NET 8, WPF, MVVM (CommunityToolkit.Mvvm), System.Data.Odbc, WPF-UI (Fluent / Mica, light and dark themes that follow Windows, with a manual toggle in the top bar), AvalonEdit.

## Troubleshooting

* **"Arithmetic operation resulted in an overflow"**: some 64-bit ODBC drivers return row counts or length values as 32-bit numbers. .NET's System.Data.Odbc then fails on every statement, *after* the driver has already run it. The connection profile's **ODBC access layer** handles this:
  * **Auto** (default) detects the problem at connect time and switches to the built-in native ODBC layer, which calls `odbc32.dll` directly and tolerates such values.
  * **Native** always uses the native layer.
  * **Managed** always uses System.Data.Odbc.

  Both layers send your SQL to the driver unchanged. The layer in use is shown in the connection details and in the DDL test report.

* **"Data source name not found and no default driver specified" (IM002)**: the driver or DSN name doesn't exist in the **64-bit** ODBC configuration. Install the 64-bit HFSQL ODBC driver, or create the DSN with the 64-bit ODBC administrator.
* **Connection works but keywords seem ignored**: HFSQL connection-string keywords differ between driver versions. Switch to *Connection string (advanced)* and adjust the keys, for example `Server Name` / `Server Port`.
* **An ALTER form fails**: that is the result the tool exists to find. The exact driver error is in the Messages tab and the report.
