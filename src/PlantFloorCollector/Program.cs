using System.Globalization;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<CollectorDb>();
builder.Services.AddHttpClient<OdooClient>();
builder.Services.AddHostedService<SimulatorWorker>();
builder.Services.AddHostedService<OdooPushWorker>();
builder.Services.AddHostedService<DriverWorker>();

var app = builder.Build();
app.UseStaticFiles();

RuntimeFolders.Ensure(builder.Configuration);

var db = app.Services.GetRequiredService<CollectorDb>();
db.Initialize();

app.MapGet("/", (CollectorDb db) => Results.Content(Html.Layout("Dashboard", Html.Dashboard(db)), "text/html"));

app.MapGet("/machines", (CollectorDb db) => Results.Content(Html.Layout("Machines", Html.Machines(db)), "text/html"));

app.MapGet("/machines/edit/{code}", (string code, CollectorDb db) =>
{
    var machine = db.GetMachine(code);
    if (machine is null)
    {
        return Results.NotFound($"Machine {code} was not found.");
    }
    return Results.Content(Html.Layout("Edit Machine", Html.EditMachine(machine)), "text/html");
});

app.MapPost("/machines", async (HttpRequest req, CollectorDb db, OdooClient client) =>
{
    var f = await req.ReadFormAsync();
    var code = f["code"].ToString();
    var name = f["name"].ToString();
    var enabled = f["enabled"] == "on";
    var sim = f["sim"] == "on";

    db.UpsertMachine(code, name, enabled, sim);
    db.EnsureDefaultTags(code);
    db.AddLog("INFO", $"Machine {code} saved locally.");

    if (db.GetSetting("odoo_enabled") == "true")
    {
        var r = await client.RegisterMachineAsync(code, name, sim);
        db.SetSetting("machine_sync_last_message", r.ok
            ? $"Machine {code} synced to Odoo. {TrimForDisplay(r.responseBody, 500)}"
            : $"Machine {code} sync failed. {r.error}; HTTP {r.statusCode}; {TrimForDisplay(r.responseBody, 1000)}");
        db.SetSetting("machine_sync_last_ok", r.ok ? "true" : "false");
        db.SetSetting("machine_sync_last_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        db.AddLog(r.ok ? "INFO" : "ERROR", db.GetSetting("machine_sync_last_message"));
    }

    return Results.Redirect("/machines");
});

app.MapPost("/machines/sync", async (CollectorDb db, OdooClient client) =>
{
    int ok = 0, fail = 0;
    var details = new List<string>();
    foreach (var m in db.GetMachines())
    {
        var r = await client.RegisterMachineAsync(m.Code, m.Name, m.Simulation);
        if (r.ok)
        {
            ok++;
            details.Add($"OK {m.Code}: {TrimForDisplay(r.responseBody, 500)}");
        }
        else
        {
            fail++;
            details.Add($"FAILED {m.Code}: {r.error}; HTTP {r.statusCode}; {TrimForDisplay(r.responseBody, 1000)}");
        }
        db.AddLog(r.ok ? "INFO" : "ERROR", r.ok ? $"Synced machine {m.Code} to Odoo." : $"Failed to sync machine {m.Code}: {r.error}");
    }
    db.SetSetting("machine_sync_last_ok", fail == 0 ? "true" : "false");
    db.SetSetting("machine_sync_last_message", $"Machine sync complete. Success: {ok}, Failed: {fail}.\n" + string.Join("\n", details));
    db.SetSetting("machine_sync_last_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    return Results.Redirect("/machines");
});

app.MapPost("/machines/import-odoo", async (CollectorDb db, OdooClient client) =>
{
    var r = await client.GetMachinesFromOdooAsync();
    if (!r.ok)
    {
        db.SetSetting("machine_sync_last_ok", "false");
        db.SetSetting("machine_sync_last_message", $"Retrieve from Odoo failed. {r.error}; HTTP {r.statusCode}; {TrimForDisplay(r.responseBody, 1000)}");
        db.SetSetting("machine_sync_last_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        db.AddLog("ERROR", db.GetSetting("machine_sync_last_message"));
        return Results.Redirect("/machines");
    }

    int imported = 0;
    var details = new List<string>();
    foreach (var m in r.machines)
    {
        db.UpsertMachine(m.Code, m.Name, true, m.Simulation);
        db.EnsureDefaultTags(m.Code);
        imported++;
        details.Add($"IMPORTED {m.Code}: {m.Name}");
    }

    db.SetSetting("machine_sync_last_ok", "true");
    db.SetSetting("machine_sync_last_message", $"Retrieved machines from Odoo. Imported/updated: {imported}.\n" + string.Join("\n", details));
    db.SetSetting("machine_sync_last_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    db.AddLog("INFO", db.GetSetting("machine_sync_last_message"));
    return Results.Redirect("/machines");
});

app.MapGet("/tags", (CollectorDb db) => Results.Content(Html.Layout("Live Tags", Html.Tags(db)), "text/html"));

app.MapGet("/tags/edit/{id:long}", (long id, CollectorDb db) =>
{
    var tag = db.GetTagDefinition(id);
    if (tag is null)
    {
        return Results.NotFound($"Tag {id} was not found.");
    }
    return Results.Content(Html.Layout("Edit Tag", Html.EditTag(db, tag)), "text/html");
});

app.MapPost("/tags", async (HttpRequest req, CollectorDb db) =>
{
    var f = await req.ReadFormAsync();
    var idText = f["id"].ToString();
    long.TryParse(idText, out var id);
    db.UpsertTagDefinition(
        id,
        f["machine"].ToString(),
        f["tag"].ToString(),
        f["data_type"].ToString(),
        f["role"].ToString(),
        f["unit"].ToString(),
        f["enabled"] == "on",
        f["simulation"] == "on",
        ParseNullableDouble(f["min"].ToString()),
        ParseNullableDouble(f["max"].ToString())
    );
    db.AddLog("INFO", $"Tag {f["machine"]}.{f["tag"]} saved.");
    return Results.Redirect("/tags");
});

app.MapPost("/tags/delete/{id:long}", (long id, CollectorDb db) =>
{
    db.DeleteTagDefinition(id);
    db.AddLog("INFO", $"Tag definition {id} deleted.");
    return Results.Redirect("/tags");
});

app.MapPost("/tags/sync-odoo", async (CollectorDb db, OdooClient client) =>
{
    var result = await client.SyncTagDefinitionsAsync();
    db.SetSetting("tag_sync_last_ok", result.ok ? "true" : "false");
    db.SetSetting("tag_sync_last_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    db.SetSetting("tag_sync_last_message", result.ok
        ? $"Tag sync succeeded. HTTP {result.statusCode}. {TrimForDisplay(result.responseBody, 1000)}"
        : $"Tag sync failed. {result.error}; HTTP {result.statusCode}; {TrimForDisplay(result.responseBody, 1500)}");
    db.AddLog(result.ok ? "INFO" : "ERROR", db.GetSetting("tag_sync_last_message"));
    return Results.Redirect("/tags");
});

app.MapPost("/tags/push-values", async (CollectorDb db, OdooClient client) =>
{
    var payload = db.BuildCurrentValuesBatchPayload();
    var result = await client.PostBatchPayloadAsync(payload);
    db.SetSetting("tag_sync_last_ok", result.ok ? "true" : "false");
    db.SetSetting("tag_sync_last_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    db.SetSetting("tag_sync_last_message", result.ok
        ? $"Current values pushed. HTTP {result.statusCode}. {TrimForDisplay(result.responseBody, 1000)}"
        : $"Current value push failed. {result.error}; HTTP {result.statusCode}; {TrimForDisplay(result.responseBody, 1500)}");
    db.AddLog(result.ok ? "INFO" : "ERROR", db.GetSetting("tag_sync_last_message"));
    return Results.Redirect("/tags");
});



app.MapGet("/mappings", (CollectorDb db) => Results.Content(Html.Layout("Tag Mappings", Html.Mappings(db)), "text/html"));

app.MapPost("/mappings", async (HttpRequest req, CollectorDb db) =>
{
    var f = await req.ReadFormAsync();
    long.TryParse(f["id"].ToString(), out var id);
    long.TryParse(f["driver_id"].ToString(), out var driverId);
    int.TryParse(f["poll_rate_ms"].ToString(), out var pollRate);
    var deadband = ParseNullableDouble(f["deadband"].ToString());
    db.UpdateTagMapping(id, driverId, f["plc_address"].ToString(), f["direction"].ToString(), Math.Max(0, pollRate), deadband, f["write_enabled"] == "on");
    db.AddLog("INFO", $"Mapping updated for tag id {id}.");
    return Results.Redirect("/mappings");
});

app.MapGet("/mappings/export", (CollectorDb db) =>
{
    var json = db.ExportMappingsJson();
    return Results.Text(json, "application/json");
});

app.MapPost("/mappings/import", async (HttpRequest req, CollectorDb db) =>
{
    var f = await req.ReadFormAsync();
    var json = f["json"].ToString();
    var result = db.ImportMappingsJson(json);
    db.SetSetting("mapping_last_ok", result.ok ? "true" : "false");
    db.SetSetting("mapping_last_message", result.message);
    db.SetSetting("mapping_last_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    db.AddLog(result.ok ? "INFO" : "ERROR", result.message);
    return Results.Redirect("/mappings");
});

app.MapGet("/drivers", (CollectorDb db) => Results.Content(Html.Layout("Drivers", Html.Drivers(db)), "text/html"));

app.MapGet("/drivers/edit/{id:long}", (long id, CollectorDb db) =>
{
    var driver = db.GetDriver(id);
    if (driver is null) return Results.NotFound($"Driver {id} was not found.");
    return Results.Content(Html.Layout("Edit Driver", Html.EditDriver(driver)), "text/html");
});

app.MapPost("/drivers", async (HttpRequest req, CollectorDb db) =>
{
    var f = await req.ReadFormAsync();
    long.TryParse(f["id"].ToString(), out var id);
    int.TryParse(f["port"].ToString(), out var port);
    int.TryParse(f["scan_rate"].ToString(), out var scanRate);
    db.UpsertDriver(id, f["name"].ToString(), f["driver_type"].ToString(), f["host"].ToString(), port, f["path"].ToString(), Math.Max(100, scanRate), f["enabled"] == "on");
    db.AddLog("INFO", $"Driver {f["name"]} saved.");
    return Results.Redirect("/drivers");
});

app.MapPost("/drivers/delete/{id:long}", (long id, CollectorDb db) =>
{
    db.DeleteDriver(id);
    db.AddLog("INFO", $"Driver {id} deleted.");
    return Results.Redirect("/drivers");
});

app.MapPost("/drivers/test/{id:long}", async (long id, CollectorDb db) =>
{
    var driver = db.GetDriver(id);
    if (driver is null)
    {
        db.SetSetting("driver_last_ok", "false");
        db.SetSetting("driver_last_message", $"Driver {id} not found.");
    }
    else
    {
        var result = await DriverTester.TestAsync(driver);
        db.SetDriverStatus(id, result.Ok ? "connected" : "error", result.Message, result.ElapsedMs);
        db.SetSetting("driver_last_ok", result.Ok ? "true" : "false");
        db.SetSetting("driver_last_message", $"{driver.Name}: {result.Message} ({result.ElapsedMs} ms)");
        db.SetSetting("driver_last_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        db.AddLog(result.Ok ? "INFO" : "ERROR", db.GetSetting("driver_last_message"));
    }
    return Results.Redirect("/drivers");
});

app.MapGet("/diagnostics", (CollectorDb db) => Results.Content(Html.Layout("Diagnostics", Html.Diagnostics(db)), "text/html"));

app.MapGet("/odoo", (CollectorDb db) => Results.Content(Html.Layout("Odoo", Html.Odoo(db)), "text/html"));

app.MapPost("/odoo", async (HttpRequest req, CollectorDb db) =>
{
    var f = await req.ReadFormAsync();
    db.SetSetting("odoo_url", f["url"].ToString());
    db.SetSetting("odoo_endpoint", f["endpoint"].ToString());
    db.SetSetting("odoo_token", f["token"].ToString());
    db.SetSetting("odoo_enabled", f["enabled"] == "on" ? "true" : "false");
    db.AddLog("INFO", "Odoo settings saved.");
    return Results.Redirect("/odoo");
});

app.MapPost("/odoo/test", async (CollectorDb db, OdooClient client) =>
{
    var result = await client.TestDetailedAsync();
    db.SetSetting("odoo_last_test_ok", result.ok ? "true" : "false");
    db.SetSetting("odoo_last_test_message", result.message);
    db.SetSetting("odoo_last_test_endpoint", result.endpoint);
    db.SetSetting("odoo_last_test_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    db.AddLog(result.ok ? "INFO" : "ERROR", result.ok ? $"Odoo test succeeded: {result.message}" : $"Odoo test failed: {result.message}");
    return Results.Redirect("/odoo?tested=1");
});

app.MapGet("/settings", (CollectorDb db) => Results.Content(Html.Layout("Settings", Html.Settings(db)), "text/html"));

app.MapPost("/settings", async (HttpRequest req, CollectorDb db) =>
{
    var f = await req.ReadFormAsync();
    db.SetSetting("collector_name", f["collector_name"].ToString());
    db.SetSetting("site_name", f["site_name"].ToString());
    db.SetSetting("simulator_interval", f["simulator_interval"].ToString());
    db.SetSetting("odoo_push_interval", f["odoo_push_interval"].ToString());
    db.SetSetting("retention_days", f["retention_days"].ToString());
    db.AddLog("INFO", "General settings saved.");
    return Results.Redirect("/settings");
});

app.MapGet("/updates", (CollectorDb db) => Results.Content(Html.Layout("Updates", Html.Updates(db)), "text/html"));

app.MapPost("/updates/settings", async (HttpRequest req, CollectorDb db) =>
{
    var f = await req.ReadFormAsync();
    db.SetSetting("update_manifest_url", f["manifest_url"].ToString());
    db.SetSetting("docker_image_name", f["docker_image"].ToString());
    db.SetSetting("auto_backup_before_update", f["auto_backup"] == "on" ? "true" : "false");
    db.SetSetting("update_last_ok", "true");
    db.SetSetting("update_last_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    db.SetSetting("update_last_message", "Update settings saved.");
    db.AddLog("INFO", "Update settings saved.");
    return Results.Redirect("/updates");
});

app.MapPost("/updates/check", async (CollectorDb db) =>
{
    var result = await CheckForUpdatesAsync(db);
    db.SetSetting("update_last_ok", result.Ok ? "true" : "false");
    db.SetSetting("update_last_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    db.SetSetting("update_last_message", result.Message);
    if (result.Manifest is not null)
    {
        db.SetSetting("update_latest_version", result.Manifest.Version ?? "");
        db.SetSetting("update_latest_image", result.Manifest.Image ?? "");
        db.SetSetting("update_latest_notes", string.Join("\n", result.Manifest.Notes ?? Array.Empty<string>()));
        db.SetSetting("update_available", IsNewerVersion(result.Manifest.Version, AppInfo.Version) ? "true" : "false");
    }
    db.AddLog(result.Ok ? "INFO" : "ERROR", result.Message);
    return Results.Redirect("/updates");
});

app.MapPost("/updates/request", async (CollectorDb db, OdooClient client) =>
{
    var image = db.GetSetting("update_latest_image");
    if (string.IsNullOrWhiteSpace(image))
    {
        db.SetSetting("update_last_ok", "false");
        db.SetSetting("update_last_message", "No update image is available. Run Check for Updates first.");
        db.SetSetting("update_last_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        return Results.Redirect("/updates");
    }

    if (db.GetSetting("auto_backup_before_update") == "true")
    {
        var backupName = $"Pre-update backup {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        var backup = await client.CreateBackupAsync(backupName);
        db.AddLog(backup.ok ? "INFO" : "ERROR", backup.ok ? $"Created {backupName} before update." : $"Pre-update backup failed: {backup.error}");
    }

    var request = new UpdateRequest(
        DateTimeOffset.UtcNow,
        AppInfo.Version,
        db.GetSetting("update_latest_version"),
        image,
        db.GetSetting("docker_image_name"),
        db.GetSetting("collector_name"));
    db.WriteUpdateRequest(request);
    db.SetSetting("update_last_ok", "true");
    db.SetSetting("update_last_message", $"Update request created for image {image}. The updater sidecar will pull/recreate the collector container.");
    db.SetSetting("update_last_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    db.AddLog("INFO", db.GetSetting("update_last_message"));
    return Results.Redirect("/updates");
});


app.MapGet("/backups", async (CollectorDb db, OdooClient client) =>
{
    var backups = new List<OdooBackupSummary>();
    var result = await client.ListBackupsAsync();
    if (result.ok)
    {
        backups = result.backups;
        db.SetSetting("backup_last_ok", "true");
        db.SetSetting("backup_last_message", $"Loaded {backups.Count} backup(s) from Odoo.");
        db.SetSetting("backup_last_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    }
    else if (!string.IsNullOrWhiteSpace(db.GetSetting("odoo_url")))
    {
        db.SetSetting("backup_last_ok", "false");
        db.SetSetting("backup_last_message", $"Backup list failed. {result.error}; HTTP {result.statusCode}; {TrimForDisplay(result.responseBody, 1500)}");
        db.SetSetting("backup_last_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    }
    return Results.Content(Html.Layout("Backups", Html.Backups(db, backups)), "text/html");
});

app.MapPost("/backups/create", async (HttpRequest req, CollectorDb db, OdooClient client) =>
{
    var f = await req.ReadFormAsync();
    var backupName = f["backup_name"].ToString();
    var r = await client.CreateBackupAsync(backupName);
    db.SetSetting("backup_last_ok", r.ok ? "true" : "false");
    db.SetSetting("backup_last_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    db.SetSetting("backup_last_message", r.ok
        ? $"Backup saved to Odoo. HTTP {r.statusCode}. {TrimForDisplay(r.responseBody, 1200)}"
        : $"Backup failed. {r.error}; HTTP {r.statusCode}; {TrimForDisplay(r.responseBody, 1500)}");
    db.AddLog(r.ok ? "INFO" : "ERROR", db.GetSetting("backup_last_message"));
    return Results.Redirect("/backups");
});

app.MapPost("/backups/restore/{id:int}", async (int id, CollectorDb db, OdooClient client) =>
{
    var r = await client.GetBackupAsync(id);
    if (!r.ok)
    {
        db.SetSetting("backup_last_ok", "false");
        db.SetSetting("backup_last_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        db.SetSetting("backup_last_message", $"Restore failed while retrieving backup {id}. {r.error}; HTTP {r.statusCode}; {TrimForDisplay(r.responseBody, 1500)}");
        db.AddLog("ERROR", db.GetSetting("backup_last_message"));
        return Results.Redirect("/backups");
    }
    var imported = db.ImportConfigurationJson(r.configJson);
    db.SetSetting("backup_last_ok", imported.ok ? "true" : "false");
    db.SetSetting("backup_last_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    db.SetSetting("backup_last_message", imported.ok ? $"Restored Odoo backup {id}. {imported.message}" : $"Restore failed for backup {id}. {imported.message}");
    db.AddLog(imported.ok ? "INFO" : "ERROR", db.GetSetting("backup_last_message"));
    return Results.Redirect("/backups");
});

app.MapGet("/backups/export-local", (CollectorDb db) => Results.Text(db.ExportConfigurationJson(), "application/json"));

app.MapGet("/queue", (CollectorDb db) => Results.Content(Html.Layout("Queue", Html.Queue(db)), "text/html"));
app.MapGet("/logs", (CollectorDb db) => Results.Content(Html.Layout("Logs", Html.Logs(db)), "text/html"));
app.MapGet("/api/status", (CollectorDb db) => Results.Json(db.Status()));
app.MapGet("/api/about", () => Results.Json(AppInfo.AsObject()));
app.MapGet("/about", () => Results.Content(Html.Layout("About", Html.About()), "text/html"));

app.Run();

static async Task<UpdateCheckResult> CheckForUpdatesAsync(CollectorDb db)
{
    var url = db.GetSetting("update_manifest_url");
    if (string.IsNullOrWhiteSpace(url))
    {
        return new UpdateCheckResult(false, "Update manifest URL is blank.", null);
    }

    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var json = await http.GetStringAsync(url);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
        {
            return new UpdateCheckResult(false, "The update manifest was returned but did not include a version.", manifest);
        }
        var current = AppInfo.Version;
        var available = IsNewerVersion(manifest.Version, current);
        var message = available
            ? $"Update available. Current: {current}. Latest: {manifest.Version}. Image: {manifest.Image}."
            : $"No update available. Current: {current}. Latest: {manifest.Version}.";
        return new UpdateCheckResult(true, message, manifest);
    }
    catch (Exception ex)
    {
        return new UpdateCheckResult(false, $"Update check failed: {ex.Message}", null);
    }
}

static bool IsNewerVersion(string? candidate, string? current)
{
    if (string.IsNullOrWhiteSpace(candidate)) return false;
    if (Version.TryParse(candidate.Trim().TrimStart('v'), out var cv) && Version.TryParse((current ?? "0.0.0").Trim().TrimStart('v'), out var cur))
    {
        return cv > cur;
    }
    return !string.Equals(candidate?.Trim(), current?.Trim(), StringComparison.OrdinalIgnoreCase);
}

static string TrimForDisplay(string? text, int maxLength)
{
    if (string.IsNullOrWhiteSpace(text)) return string.Empty;
    var cleaned = text.Replace("\r", " ").Replace("\n", " ").Trim();
    return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength] + "...";
}

static double? ParseNullableDouble(string? text)
{
    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return value;
    return null;
}


public static class AppInfo
{
    public static string Version => FirstNonEmpty(
        Environment.GetEnvironmentVariable("APP_VERSION"),
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0],
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3),
        "dev");

    public static string BuildDate => FirstNonEmpty(
        Environment.GetEnvironmentVariable("APP_BUILD_DATE"),
        "unknown");

    public static string Commit => FirstNonEmpty(
        Environment.GetEnvironmentVariable("APP_COMMIT"),
        "unknown");

    public static string Image => FirstNonEmpty(
        Environment.GetEnvironmentVariable("APP_IMAGE"),
        "unknown");

    public static object AsObject() => new
    {
        product = "Plant Floor Collector",
        version = Version,
        buildDate = BuildDate,
        commit = Commit,
        image = Image,
        runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        processStartTime = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToString("O")
    };

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return string.Empty;
    }
}

public sealed record FolderStatus(string Name, string Path, bool Exists, bool Writable, string Message);

public static class RuntimeFolders
{
    public static readonly (string Name, string ConfigKey, string DefaultPath, bool Required)[] Definitions =
    [
        ("Config", "Collector:ConfigPath", "/app/config", true),
        ("Data", "Collector:DataPath", "/app/data", true),
        ("Logs", "Collector:LogsPath", "/app/logs", true),
        ("Backups", "Collector:BackupsPath", "/app/backups", true),
        ("Drivers", "Collector:DriversPath", "/app/drivers", true),
        ("Certificates", "Collector:CertificatesPath", "/app/certs", true),
        ("Temp", "Collector:TempPath", "/app/temp", true)
    ];

    public static void Ensure(IConfiguration cfg)
    {
        foreach (var folder in Definitions)
        {
            var path = Resolve(cfg, folder.ConfigKey, folder.DefaultPath);
            Directory.CreateDirectory(path);
            VerifyWritable(path);
        }
    }

    public static List<FolderStatus> Statuses(IConfiguration cfg)
    {
        var list = new List<FolderStatus>();
        foreach (var folder in Definitions)
        {
            var path = Resolve(cfg, folder.ConfigKey, folder.DefaultPath);
            var exists = Directory.Exists(path);
            var writable = false;
            var message = exists ? "Exists" : "Missing";
            if (exists)
            {
                try
                {
                    VerifyWritable(path);
                    writable = true;
                    message = "Writable";
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                }
            }
            list.Add(new FolderStatus(folder.Name, path, exists, writable, message));
        }
        return list;
    }

    public static string Resolve(IConfiguration cfg, string key, string defaultPath)
    {
        var configured = cfg[key];
        return string.IsNullOrWhiteSpace(configured) ? defaultPath : configured;
    }

    private static void VerifyWritable(string path)
    {
        var testPath = Path.Combine(path, $".write-test-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(testPath, DateTimeOffset.UtcNow.ToString("O"));
        File.Delete(testPath);
    }
}

public sealed class CollectorDb(IConfiguration cfg)
{
    private readonly IConfiguration _cfg = cfg;
    private readonly string _path = cfg["Collector:DatabasePath"] ?? "plant_floor_collector.db";
    private SqliteConnection Conn() => new($"Data Source={_path}");

    public void Initialize()
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT);
CREATE TABLE IF NOT EXISTS machines(code TEXT PRIMARY KEY,name TEXT NOT NULL,enabled INTEGER NOT NULL DEFAULT 1,simulation INTEGER NOT NULL DEFAULT 0,status TEXT DEFAULT 'offline',last_update TEXT);
CREATE TABLE IF NOT EXISTS tag_definitions(id INTEGER PRIMARY KEY AUTOINCREMENT,machine_code TEXT NOT NULL,tag TEXT NOT NULL,data_type TEXT NOT NULL DEFAULT 'float',role TEXT DEFAULT '',unit TEXT DEFAULT '',enabled INTEGER NOT NULL DEFAULT 1,simulation INTEGER NOT NULL DEFAULT 1,min_value REAL,max_value REAL,driver_id INTEGER DEFAULT 0,plc_address TEXT DEFAULT '',direction TEXT DEFAULT 'read',poll_rate_ms INTEGER DEFAULT 1000,deadband REAL,write_enabled INTEGER NOT NULL DEFAULT 0,created_at TEXT DEFAULT CURRENT_TIMESTAMP,updated_at TEXT,UNIQUE(machine_code,tag));
CREATE TABLE IF NOT EXISTS tag_values(id INTEGER PRIMARY KEY AUTOINCREMENT,machine_code TEXT,tag TEXT,value TEXT,quality TEXT,ts TEXT);
CREATE TABLE IF NOT EXISTS queue(id INTEGER PRIMARY KEY AUTOINCREMENT,payload TEXT NOT NULL,created_at TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,last_error TEXT);
CREATE TABLE IF NOT EXISTS logs(id INTEGER PRIMARY KEY AUTOINCREMENT,level TEXT,message TEXT,ts TEXT);
CREATE TABLE IF NOT EXISTS drivers(id INTEGER PRIMARY KEY AUTOINCREMENT,name TEXT NOT NULL,driver_type TEXT NOT NULL,host TEXT DEFAULT '',port INTEGER DEFAULT 0,path TEXT DEFAULT '',scan_rate_ms INTEGER DEFAULT 1000,enabled INTEGER NOT NULL DEFAULT 1,status TEXT DEFAULT 'offline',last_error TEXT DEFAULT '',last_scan_ms INTEGER DEFAULT 0,last_update TEXT);
CREATE TABLE IF NOT EXISTS machine_status_cache(machine_code TEXT PRIMARY KEY,status_key TEXT NOT NULL,last_sent TEXT NOT NULL);";
        cmd.ExecuteNonQuery();

        EnsureColumn("tag_definitions", "driver_id", "INTEGER DEFAULT 0");
        EnsureColumn("tag_definitions", "plc_address", "TEXT DEFAULT ''");
        EnsureColumn("tag_definitions", "direction", "TEXT DEFAULT 'read'");
        EnsureColumn("tag_definitions", "poll_rate_ms", "INTEGER DEFAULT 1000");
        EnsureColumn("tag_definitions", "deadband", "REAL");
        EnsureColumn("tag_definitions", "write_enabled", "INTEGER NOT NULL DEFAULT 0");

        SetDefault("odoo_endpoint", "/plant_floor_monitor/api/v1/batch");
        SetDefault("odoo_enabled", "false");
        SetDefault("collector_name", "Plant Floor Collector");
        SetDefault("site_name", "Main Plant");
        SetDefault("simulator_interval", "5");
        SetDefault("odoo_push_interval", "10");
        SetDefault("retention_days", "30");
        SetSetting("collector_version", AppInfo.Version);
        SetSetting("build_date", AppInfo.BuildDate);
        SetSetting("git_commit", AppInfo.Commit);
        SetSetting("docker_image", AppInfo.Image);
        SetDefault("update_manifest_url", "http://localhost:8081/plant-floor-collector/version.json");
        SetDefault("docker_image_name", "plant-floor-collector");
        SetDefault("auto_backup_before_update", "true");

        if (GetMachines().Count == 0)
        {
            UpsertMachine("SIM01", "Simulated Packaging Line", true, true);
            UpsertMachine("SIM02", "Simulated Press", true, true);
            AddLog("INFO", "Created default simulated machines SIM01 and SIM02.");
        }

        foreach (var machine in GetMachines())
        {
            EnsureDefaultTags(machine.Code);
        }

        if (GetDrivers().Count == 0)
        {
            UpsertDriver(0, "Simulation Driver", "simulation", "", 0, "", 1000, true);
            UpsertDriver(0, "Modbus TCP Template", "modbus_tcp", "192.168.1.10", 502, "UnitId=1", 1000, false);
            UpsertDriver(0, "Allen-Bradley Template", "allen_bradley", "192.168.1.24", 44818, "1,0", 1000, false);
            UpsertDriver(0, "Mitsubishi MC Template", "mitsubishi_mc", "192.168.1.250", 1240, "binary", 1000, false);
            AddLog("INFO", "Created default driver templates.");
        }
    }


    private void EnsureColumn(string table, string column, string definition)
    {
        using var c = Conn();
        c.Open();
        using var check = c.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        using var r = check.ExecuteReader();
        while (r.Read())
        {
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        }
        using var alter = c.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    private void SetDefault(string key, string value)
    {
        if (string.IsNullOrEmpty(GetSetting(key))) SetSetting(key, value);
    }

    public string GetSetting(string key)
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    public void SetSetting(string key, string value)
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value ?? "");
        cmd.ExecuteNonQuery();
    }

    public List<FolderStatus> FolderStatuses() => RuntimeFolders.Statuses(_cfg);

    public string DataDirectory()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        return string.IsNullOrWhiteSpace(dir) ? Directory.GetCurrentDirectory() : dir;
    }

    public string UpdateRequestPath() => Path.Combine(DataDirectory(), "update-request.json");

    public void WriteUpdateRequest(UpdateRequest request)
    {
        Directory.CreateDirectory(DataDirectory());
        File.WriteAllText(UpdateRequestPath(), JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true }));
    }

    public string ReadUpdateRequest()
    {
        var path = UpdateRequestPath();
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    public List<Machine> GetMachines()
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT code,name,enabled,simulation,status,COALESCE(last_update,'') FROM machines ORDER BY code";
        using var r = cmd.ExecuteReader();
        var list = new List<Machine>();
        while (r.Read())
        {
            list.Add(new Machine(r.GetString(0), r.GetString(1), r.GetInt32(2) == 1, r.GetInt32(3) == 1, r.GetString(4), r.GetString(5)));
        }
        return list;
    }

    public Machine? GetMachine(string code)
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT code,name,enabled,simulation,status,COALESCE(last_update,'') FROM machines WHERE code=$c LIMIT 1";
        cmd.Parameters.AddWithValue("$c", NormalizeCode(code));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new Machine(r.GetString(0), r.GetString(1), r.GetInt32(2) == 1, r.GetInt32(3) == 1, r.GetString(4), r.GetString(5)) : null;
    }

    public void UpsertMachine(string code, string name, bool enabled, bool sim)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO machines(code,name,enabled,simulation,status,last_update) VALUES($c,$n,$e,$s,'offline',datetime('now')) ON CONFLICT(code) DO UPDATE SET name=$n,enabled=$e,simulation=$s,last_update=datetime('now')";
        cmd.Parameters.AddWithValue("$c", NormalizeCode(code));
        cmd.Parameters.AddWithValue("$n", string.IsNullOrWhiteSpace(name) ? NormalizeCode(code) : name.Trim());
        cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$s", sim ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void EnsureDefaultTags(string machineCode)
    {
        var code = NormalizeCode(machineCode);
        if (string.IsNullOrWhiteSpace(code)) return;
        if (GetTagDefinitions(code).Count > 0) return;

        UpsertTagDefinition(0, code, "Running", "boolean", "running", "", true, true, 0, 1);
        UpsertTagDefinition(0, code, "Status", "string", "status", "", true, true, null, null);
        UpsertTagDefinition(0, code, "PartCount", "integer", "count", "parts", true, true, 1000, 50000);
        UpsertTagDefinition(0, code, "Speed", "float", "speed", "ppm", true, true, 40, 120);
        UpsertTagDefinition(0, code, "OEE", "float", "oee", "%", true, true, 65, 98);
        UpsertTagDefinition(0, code, "Temperature", "float", "temperature", "F", true, true, 65, 95);
        UpsertTagDefinition(0, code, "Alarm", "boolean", "alarm", "", true, true, 0, 1);
    }

    public List<TagDefinition> GetTagDefinitions(string? machineCode = null)
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        if (string.IsNullOrWhiteSpace(machineCode))
        {
            cmd.CommandText = "SELECT id,machine_code,tag,data_type,COALESCE(role,''),COALESCE(unit,''),enabled,simulation,min_value,max_value,COALESCE(driver_id,0),COALESCE(plc_address,''),COALESCE(direction,'read'),COALESCE(poll_rate_ms,1000),deadband,COALESCE(write_enabled,0) FROM tag_definitions ORDER BY machine_code,tag";
        }
        else
        {
            cmd.CommandText = "SELECT id,machine_code,tag,data_type,COALESCE(role,''),COALESCE(unit,''),enabled,simulation,min_value,max_value,COALESCE(driver_id,0),COALESCE(plc_address,''),COALESCE(direction,'read'),COALESCE(poll_rate_ms,1000),deadband,COALESCE(write_enabled,0) FROM tag_definitions WHERE machine_code=$m ORDER BY tag";
            cmd.Parameters.AddWithValue("$m", NormalizeCode(machineCode));
        }
        using var r = cmd.ExecuteReader();
        var list = new List<TagDefinition>();
        while (r.Read())
        {
            list.Add(ReadTagDefinition(r));
        }
        return list;
    }

    public TagDefinition? GetTagDefinition(long id)
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,machine_code,tag,data_type,COALESCE(role,''),COALESCE(unit,''),enabled,simulation,min_value,max_value,COALESCE(driver_id,0),COALESCE(plc_address,''),COALESCE(direction,'read'),COALESCE(poll_rate_ms,1000),deadband,COALESCE(write_enabled,0) FROM tag_definitions WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadTagDefinition(r) : null;
    }

    private static TagDefinition ReadTagDefinition(SqliteDataReader r)
    {
        return new TagDefinition(
            r.GetInt64(0),
            r.GetString(1),
            r.GetString(2),
            r.GetString(3),
            r.GetString(4),
            r.GetString(5),
            r.GetInt32(6) == 1,
            r.GetInt32(7) == 1,
            r.IsDBNull(8) ? null : r.GetDouble(8),
            r.IsDBNull(9) ? null : r.GetDouble(9),
            r.GetInt64(10),
            r.GetString(11),
            r.GetString(12),
            r.GetInt32(13),
            r.IsDBNull(14) ? null : r.GetDouble(14),
            r.GetInt32(15) == 1
        );
    }

    public void UpsertTagDefinition(long id, string machineCode, string tag, string dataType, string role, string unit, bool enabled, bool simulation, double? min, double? max)
    {
        var machine = NormalizeCode(machineCode);
        var tagName = (tag ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(machine) || string.IsNullOrWhiteSpace(tagName)) return;

        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        if (id > 0)
        {
            cmd.CommandText = @"UPDATE tag_definitions SET machine_code=$m,tag=$t,data_type=$dt,role=$r,unit=$u,enabled=$e,simulation=$s,min_value=$min,max_value=$max,updated_at=datetime('now') WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
        }
        else
        {
            cmd.CommandText = @"INSERT INTO tag_definitions(machine_code,tag,data_type,role,unit,enabled,simulation,min_value,max_value,updated_at) VALUES($m,$t,$dt,$r,$u,$e,$s,$min,$max,datetime('now')) ON CONFLICT(machine_code,tag) DO UPDATE SET data_type=$dt,role=$r,unit=$u,enabled=$e,simulation=$s,min_value=$min,max_value=$max,updated_at=datetime('now')";
        }
        cmd.Parameters.AddWithValue("$m", machine);
        cmd.Parameters.AddWithValue("$t", tagName);
        cmd.Parameters.AddWithValue("$dt", string.IsNullOrWhiteSpace(dataType) ? "float" : dataType.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$r", role?.Trim().ToLowerInvariant() ?? "");
        cmd.Parameters.AddWithValue("$u", unit?.Trim() ?? "");
        cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$s", simulation ? 1 : 0);
        cmd.Parameters.AddWithValue("$min", min.HasValue ? min.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$max", max.HasValue ? max.Value : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void DeleteTagDefinition(long id)
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM tag_definitions WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SaveTag(string machine, string tag, object? value, string quality = "GOOD")
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO tag_values(machine_code,tag,value,quality,ts) VALUES($m,$t,$v,$q,datetime('now'));
UPDATE machines SET status=CASE WHEN $t='Status' THEN $v ELSE status END,last_update=datetime('now') WHERE code=$m;";
        cmd.Parameters.AddWithValue("$m", NormalizeCode(machine));
        cmd.Parameters.AddWithValue("$t", tag);
        cmd.Parameters.AddWithValue("$v", value?.ToString() ?? "");
        cmd.Parameters.AddWithValue("$q", quality);
        cmd.ExecuteNonQuery();
    }

    public List<TagValue> LatestTags()
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT tv.machine_code,tv.tag,tv.value,tv.quality,tv.ts
FROM tag_values tv
JOIN (
    SELECT machine_code,tag,MAX(id) AS max_id FROM tag_values GROUP BY machine_code,tag
) latest ON latest.max_id=tv.id
ORDER BY tv.machine_code,tv.tag";
        using var r = cmd.ExecuteReader();
        var list = new List<TagValue>();
        while (r.Read())
        {
            list.Add(new TagValue(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        }
        return list;
    }

    public Dictionary<string, TagValue> LatestTagMap()
    {
        return LatestTags().ToDictionary(x => $"{x.Machine}.{x.Tag}", x => x, StringComparer.OrdinalIgnoreCase);
    }

    public string BuildCurrentValuesBatchPayload()
    {
        var latest = LatestTagMap();
        var machines = new List<Dictionary<string, object?>>();
        foreach (var machine in GetMachines().Where(x => x.Enabled))
        {
            var tagObjects = new List<object>();
            foreach (var tag in GetTagDefinitions(machine.Code).Where(t => t.Enabled))
            {
                latest.TryGetValue($"{machine.Code}.{tag.Tag}", out var value);
                tagObjects.Add(new
                {
                    tag = tag.Tag,
                    value = value?.Value ?? string.Empty,
                    quality = value?.Quality ?? "UNKNOWN",
                    data_type = tag.DataType,
                    role = tag.Role,
                    unit = tag.Unit,
                    timestamp = value?.Ts ?? DateTimeOffset.UtcNow.ToString("O")
                });
            }
            if (tagObjects.Count > 0)
            {
                var communicationStatus = machine.Simulation ? "connected" : "manual";
                var payload = BuildIngestEnvelope(machine.Code, machine.Name, tagObjects, machine.Status, communicationStatus, "", "");
                machines.Add(payload);
            }
        }
        return JsonSerializer.Serialize(new { batch_id = Guid.NewGuid().ToString("N"), collector = CollectorCode(), ingest = machines });
    }

    public Dictionary<string, object?> BuildIngestEnvelope(string machineCode, string machineName, object tags, string status, string communicationStatus, string alarmStatus, string statusMessage)
    {
        var payload = new Dictionary<string, object?>
        {
            ["collector"] = CollectorCode(),
            ["collector_name"] = GetSetting("collector_name"),
            ["machine"] = NormalizeCode(machineCode),
            ["machine_name"] = machineName,
            ["timestamp"] = DateTimeOffset.UtcNow,
            ["tags"] = tags
        };

        if (ShouldSendMachineStatus(machineCode, status, communicationStatus, alarmStatus, statusMessage))
        {
            payload["status"] = status;
            payload["communication_status"] = communicationStatus;
            if (!string.IsNullOrWhiteSpace(alarmStatus)) payload["alarm_status"] = alarmStatus;
            if (!string.IsNullOrWhiteSpace(statusMessage)) payload["status_message"] = statusMessage;
            payload["status_changed"] = true;
        }
        else
        {
            payload["status_changed"] = false;
        }

        return payload;
    }

    public bool ShouldSendMachineStatus(string machineCode, string status, string communicationStatus, string alarmStatus, string statusMessage)
    {
        var code = NormalizeCode(machineCode);
        var key = string.Join("|", status ?? "", communicationStatus ?? "", alarmStatus ?? "", statusMessage ?? "");
        using var c = Conn();
        c.Open();
        using (var check = c.CreateCommand())
        {
            check.CommandText = "SELECT status_key FROM machine_status_cache WHERE machine_code=$m";
            check.Parameters.AddWithValue("$m", code);
            var existing = check.ExecuteScalar()?.ToString();
            if (existing == key) return false;
        }
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO machine_status_cache(machine_code,status_key,last_sent) VALUES($m,$k,datetime('now')) ON CONFLICT(machine_code) DO UPDATE SET status_key=$k,last_sent=datetime('now')";
        cmd.Parameters.AddWithValue("$m", code);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
        return true;
    }

    public string BuildTagDefinitionsBatchPayload()
    {
        var groups = GetTagDefinitions().Where(t => t.Enabled).GroupBy(t => t.MachineCode);
        var tagPayloads = groups.Select(g => new
        {
            collector = CollectorCode(),
            machine = g.Key,
            tags = g.Select(t => new
            {
                tag = t.Tag,
                name = t.Tag,
                display_name = t.Tag,
                data_type = t.DataType,
                role = t.Role,
                unit = t.Unit,
                driver_id = t.DriverId,
                plc_address = t.PlcAddress,
                direction = t.Direction,
                poll_rate_ms = t.PollRateMs,
                deadband = t.Deadband,
                write_enabled = t.WriteEnabled,
                enabled = t.Enabled,
                log_history = true
            }).ToArray()
        }).ToArray();
        return JsonSerializer.Serialize(new { batch_id = Guid.NewGuid().ToString("N"), collector = CollectorCode(), tags = tagPayloads });
    }

    public void Enqueue(string payload)
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO queue(payload,created_at) VALUES($p,datetime('now'))";
        cmd.Parameters.AddWithValue("$p", payload);
        cmd.ExecuteNonQuery();
    }

    public QueueItem? NextQueue()
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,payload,attempts FROM queue ORDER BY id LIMIT 1";
        using var r = cmd.ExecuteReader();
        return r.Read() ? new QueueItem(r.GetInt64(0), r.GetString(1), r.GetInt32(2)) : null;
    }

    public void DeleteQueue(long id)
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM queue WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void FailQueue(long id, string err)
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE queue SET attempts=attempts+1,last_error=$e WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$e", err);
        cmd.ExecuteNonQuery();
    }

    public List<string[]> QueueRows()
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,created_at,attempts,COALESCE(last_error,''),substr(payload,1,180) FROM queue ORDER BY id DESC LIMIT 100";
        using var r = cmd.ExecuteReader();
        var list = new List<string[]>();
        while (r.Read())
        {
            list.Add(new[] { r.GetInt64(0).ToString(), r.GetString(1), r.GetInt32(2).ToString(), r.GetString(3), r.GetString(4) });
        }
        return list;
    }


    public List<DriverDefinition> GetDrivers()
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,name,driver_type,COALESCE(host,''),COALESCE(port,0),COALESCE(path,''),COALESCE(scan_rate_ms,1000),enabled,COALESCE(status,'offline'),COALESCE(last_error,''),COALESCE(last_scan_ms,0),COALESCE(last_update,'') FROM drivers ORDER BY name";
        using var r = cmd.ExecuteReader();
        var list = new List<DriverDefinition>();
        while (r.Read())
        {
            list.Add(new DriverDefinition(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetInt32(6), r.GetInt32(7) == 1, r.GetString(8), r.GetString(9), r.GetInt32(10), r.GetString(11)));
        }
        return list;
    }

    public DriverDefinition? GetDriver(long id)
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,name,driver_type,COALESCE(host,''),COALESCE(port,0),COALESCE(path,''),COALESCE(scan_rate_ms,1000),enabled,COALESCE(status,'offline'),COALESCE(last_error,''),COALESCE(last_scan_ms,0),COALESCE(last_update,'') FROM drivers WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new DriverDefinition(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetInt32(6), r.GetInt32(7) == 1, r.GetString(8), r.GetString(9), r.GetInt32(10), r.GetString(11)) : null;
    }

    public void UpsertDriver(long id, string name, string driverType, string host, int port, string path, int scanRateMs, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        if (id > 0)
        {
            cmd.CommandText = @"UPDATE drivers SET name=$n,driver_type=$t,host=$h,port=$p,path=$pa,scan_rate_ms=$s,enabled=$e,last_update=datetime('now') WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
        }
        else
        {
            cmd.CommandText = @"INSERT INTO drivers(name,driver_type,host,port,path,scan_rate_ms,enabled,status,last_update) VALUES($n,$t,$h,$p,$pa,$s,$e,'offline',datetime('now'))";
        }
        cmd.Parameters.AddWithValue("$n", name.Trim());
        cmd.Parameters.AddWithValue("$t", string.IsNullOrWhiteSpace(driverType) ? "simulation" : driverType.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$h", host?.Trim() ?? "");
        cmd.Parameters.AddWithValue("$p", port);
        cmd.Parameters.AddWithValue("$pa", path?.Trim() ?? "");
        cmd.Parameters.AddWithValue("$s", scanRateMs <= 0 ? 1000 : scanRateMs);
        cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void DeleteDriver(long id)
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM drivers WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetDriverStatus(long id, string status, string lastError, int elapsedMs)
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE drivers SET status=$s,last_error=$e,last_scan_ms=$ms,last_update=datetime('now') WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$e", lastError ?? "");
        cmd.Parameters.AddWithValue("$ms", elapsedMs);
        cmd.ExecuteNonQuery();
    }


    public void UpdateTagMapping(long tagId, long driverId, string plcAddress, string direction, int pollRateMs, double? deadband, bool writeEnabled)
    {
        if (tagId <= 0) return;
        var dir = string.IsNullOrWhiteSpace(direction) ? "read" : direction.Trim().ToLowerInvariant();
        if (dir is not ("read" or "write" or "readwrite")) dir = "read";
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"UPDATE tag_definitions SET driver_id=$d,plc_address=$a,direction=$dir,poll_rate_ms=$p,deadband=$db,write_enabled=$w,updated_at=datetime('now') WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", tagId);
        cmd.Parameters.AddWithValue("$d", driverId);
        cmd.Parameters.AddWithValue("$a", plcAddress?.Trim() ?? "");
        cmd.Parameters.AddWithValue("$dir", dir);
        cmd.Parameters.AddWithValue("$p", pollRateMs <= 0 ? 1000 : pollRateMs);
        cmd.Parameters.AddWithValue("$db", deadband.HasValue ? deadband.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$w", writeEnabled ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public string ExportMappingsJson()
    {
        var mappings = GetTagDefinitions().Select(t => new
        {
            machine = t.MachineCode,
            tag = t.Tag,
            driver_id = t.DriverId,
            plc_address = t.PlcAddress,
            direction = t.Direction,
            poll_rate_ms = t.PollRateMs,
            deadband = t.Deadband,
            write_enabled = t.WriteEnabled
        });
        return JsonSerializer.Serialize(new { exported_at = DateTimeOffset.UtcNow, mappings }, new JsonSerializerOptions { WriteIndented = true });
    }

    public (bool ok, string message) ImportMappingsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (false, "No JSON was provided.");
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("mappings", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return (false, "JSON must contain a mappings array.");
            var count = 0;
            foreach (var item in arr.EnumerateArray())
            {
                var machine = item.TryGetProperty("machine", out var m) ? m.GetString() ?? "" : "";
                var tag = item.TryGetProperty("tag", out var tg) ? tg.GetString() ?? "" : "";
                var existing = GetTagDefinitions(machine).FirstOrDefault(x => string.Equals(x.Tag, tag, StringComparison.OrdinalIgnoreCase));
                if (existing is null) continue;
                var driverId = item.TryGetProperty("driver_id", out var d) && d.TryGetInt64(out var did) ? did : existing.DriverId;
                var address = item.TryGetProperty("plc_address", out var a) ? a.GetString() ?? existing.PlcAddress : existing.PlcAddress;
                var direction = item.TryGetProperty("direction", out var dir) ? dir.GetString() ?? existing.Direction : existing.Direction;
                var poll = item.TryGetProperty("poll_rate_ms", out var pr) && pr.TryGetInt32(out var p) ? p : existing.PollRateMs;
                double? deadband = existing.Deadband;
                if (item.TryGetProperty("deadband", out var dbv) && dbv.ValueKind == JsonValueKind.Number && dbv.TryGetDouble(out var dbd)) deadband = dbd;
                var writeEnabled = item.TryGetProperty("write_enabled", out var w) ? w.ValueKind == JsonValueKind.True : existing.WriteEnabled;
                UpdateTagMapping(existing.Id, driverId, address, direction, poll, deadband, writeEnabled);
                count++;
            }
            return (true, $"Imported/updated {count} mapping(s).");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }


    public string ExportConfigurationJson()
    {
        var payload = new
        {
            format = "plant_floor_collector_config",
            version = AppInfo.Version,
            exported_at = DateTimeOffset.UtcNow,
            collector_code = CollectorCode(),
            settings = new Dictionary<string, string>
            {
                ["collector_name"] = GetSetting("collector_name"),
                ["site_name"] = GetSetting("site_name"),
                ["simulator_interval"] = GetSetting("simulator_interval"),
                ["odoo_push_interval"] = GetSetting("odoo_push_interval"),
                ["retention_days"] = GetSetting("retention_days"),
                ["odoo_url"] = GetSetting("odoo_url"),
                ["odoo_endpoint"] = GetSetting("odoo_endpoint"),
                ["odoo_enabled"] = GetSetting("odoo_enabled")
            },
            machines = GetMachines(),
            drivers = GetDrivers(),
            tags = GetTagDefinitions()
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public (bool ok, string message) ImportConfigurationJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (false, "Backup payload was empty.");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in settings.EnumerateObject())
                {
                    if (prop.Name.Equals("odoo_token", StringComparison.OrdinalIgnoreCase)) continue;
                    SetSetting(prop.Name, prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.ToString());
                }
            }
            var machineCount = 0;
            if (root.TryGetProperty("machines", out var machines) && machines.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in machines.EnumerateArray())
                {
                    var code = GetJsonString(m, "Code", "code");
                    var name = GetJsonString(m, "Name", "name");
                    var enabled = GetJsonBool(m, true, "Enabled", "enabled");
                    var sim = GetJsonBool(m, false, "Simulation", "simulation");
                    if (!string.IsNullOrWhiteSpace(code)) { UpsertMachine(code, name, enabled, sim); machineCount++; }
                }
            }
            var driverCount = 0;
            var driverIdMap = new Dictionary<long, long>();
            if (root.TryGetProperty("drivers", out var drivers) && drivers.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in drivers.EnumerateArray())
                {
                    var oldId = GetJsonLong(d, 0, "Id", "id");
                    var name = GetJsonString(d, "Name", "name");
                    var type = GetJsonString(d, "DriverType", "driver_type");
                    var host = GetJsonString(d, "Host", "host");
                    var port = GetJsonInt(d, 0, "Port", "port");
                    var path = GetJsonString(d, "Path", "path");
                    var scan = GetJsonInt(d, 1000, "ScanRateMs", "scan_rate_ms");
                    var enabled = GetJsonBool(d, true, "Enabled", "enabled");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        var newId = UpsertDriverReturnId(0, name, type, host, port, path, scan, enabled);
                        if (oldId > 0) driverIdMap[oldId] = newId;
                        driverCount++;
                    }
                }
            }
            var tagCount = 0;
            if (root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tags.EnumerateArray())
                {
                    var machine = GetJsonString(t, "MachineCode", "machine_code", "machine");
                    var tag = GetJsonString(t, "Tag", "tag");
                    var dataType = GetJsonString(t, "DataType", "data_type");
                    var role = GetJsonString(t, "Role", "role");
                    var unit = GetJsonString(t, "Unit", "unit");
                    var enabled = GetJsonBool(t, true, "Enabled", "enabled");
                    var sim = GetJsonBool(t, true, "Simulation", "simulation");
                    var min = GetJsonDoubleNullable(t, "MinValue", "min_value", "min");
                    var max = GetJsonDoubleNullable(t, "MaxValue", "max_value", "max");
                    if (!string.IsNullOrWhiteSpace(machine) && !string.IsNullOrWhiteSpace(tag))
                    {
                        UpsertTagDefinition(0, machine, tag, dataType, role, unit, enabled, sim, min, max);
                        var existing = GetTagDefinitions(machine).FirstOrDefault(x => string.Equals(x.Tag, tag, StringComparison.OrdinalIgnoreCase));
                        if (existing is not null)
                        {
                            var oldDriver = GetJsonLong(t, 0, "DriverId", "driver_id");
                            var driverId = driverIdMap.TryGetValue(oldDriver, out var mapped) ? mapped : oldDriver;
                            UpdateTagMapping(existing.Id, driverId, GetJsonString(t, "PlcAddress", "plc_address"), GetJsonString(t, "Direction", "direction"), GetJsonInt(t, 1000, "PollRateMs", "poll_rate_ms"), GetJsonDoubleNullable(t, "Deadband", "deadband"), GetJsonBool(t, false, "WriteEnabled", "write_enabled"));
                        }
                        tagCount++;
                    }
                }
            }
            AddLog("INFO", $"Configuration restore complete. Machines={machineCount}, Drivers={driverCount}, Tags={tagCount}.");
            return (true, $"Machines={machineCount}, Drivers={driverCount}, Tags={tagCount}.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public long UpsertDriverReturnId(long id, string name, string driverType, string host, int port, string path, int scanRateMs, bool enabled)
    {
        UpsertDriver(id, name, driverType, host, port, path, scanRateMs, enabled);
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM drivers WHERE name=$n ORDER BY id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$n", name.Trim());
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
    }

    private static string GetJsonString(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v)) return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
        return "";
    }
    private static bool GetJsonBool(JsonElement e, bool def, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v))
                return v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true) || (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i != 0);
        return def;
    }
    private static int GetJsonInt(JsonElement e, int def, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out i)) return i;
            }
        return def;
    }
    private static long GetJsonLong(JsonElement e, long def, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i)) return i;
                if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out i)) return i;
            }
        return def;
    }
    private static double? GetJsonDoubleNullable(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
                if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            }
        return null;
    }

    public void AddLog(string level, string msg)
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO logs(level,message,ts) VALUES($l,$m,datetime('now'))";
        cmd.Parameters.AddWithValue("$l", level);
        cmd.Parameters.AddWithValue("$m", msg);
        cmd.ExecuteNonQuery();
    }

    public List<string[]> Logs()
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ts,level,message FROM logs ORDER BY id DESC LIMIT 200";
        using var r = cmd.ExecuteReader();
        var list = new List<string[]>();
        while (r.Read())
        {
            list.Add(new[] { r.GetString(0), r.GetString(1), r.GetString(2) });
        }
        return list;
    }

    public object Status() => new
    {
        machines = GetMachines().Count,
        tagDefinitions = GetTagDefinitions().Count,
        liveTags = LatestTags().Count,
        drivers = GetDrivers().Count,
        queue = QueueRows().Count,
        odooEnabled = GetSetting("odoo_enabled")
    };

    public string CollectorCode()
    {
        var collector = GetSetting("collector_name");
        if (string.IsNullOrWhiteSpace(collector)) collector = "Plant Floor Collector";
        return collector.Replace(" ", "_").Replace("-", "_").ToUpperInvariant();
    }

    private static string NormalizeCode(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();
}

public record Machine(string Code, string Name, bool Enabled, bool Simulation, string Status, string LastUpdate);
public record OdooMachine(string Code, string Name, bool Simulation);
public record TagDefinition(long Id, string MachineCode, string Tag, string DataType, string Role, string Unit, bool Enabled, bool Simulation, double? MinValue, double? MaxValue, long DriverId, string PlcAddress, string Direction, int PollRateMs, double? Deadband, bool WriteEnabled);
public record TagValue(string Machine, string Tag, string Value, string Quality, string Ts);
public record QueueItem(long Id, string Payload, int Attempts);
public record DriverDefinition(long Id, string Name, string DriverType, string Host, int Port, string Path, int ScanRateMs, bool Enabled, string Status, string LastError, int LastScanMs, string LastUpdate);
public record OdooBackupSummary(int Id, string Name, string CollectorCode, string CreatedAt, int MachineCount, int TagCount, int DriverCount);

public sealed class SimulatorWorker(IServiceProvider sp, IConfiguration cfg) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var rnd = new Random();
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CollectorDb>();
            foreach (var machine in db.GetMachines().Where(x => x.Enabled && x.Simulation))
            {
                db.EnsureDefaultTags(machine.Code);
                var generated = GenerateMachineValues(db, machine, rnd);
                foreach (var item in generated)
                {
                    db.SaveTag(machine.Code, item.Tag, item.Value, item.Quality);
                }
                var status = generated.FirstOrDefault(x => x.Tag.Equals("Status", StringComparison.OrdinalIgnoreCase)).Value?.ToString() ?? "running";
                var alarm = generated.FirstOrDefault(x => x.Role.Equals("alarm", StringComparison.OrdinalIgnoreCase)).Value?.ToString() ?? "False";
                var alarmStatus = string.Equals(alarm, "True", StringComparison.OrdinalIgnoreCase) ? "alarm" : "normal";
                var tagPayload = generated.Select(x => new { tag = x.Tag, value = x.Value, quality = x.Quality, data_type = x.DataType, role = x.Role, unit = x.Unit }).ToArray();
                var machinePayload = db.BuildIngestEnvelope(machine.Code, machine.Name, tagPayload, status, "connected", alarmStatus, "");
                db.Enqueue(JsonSerializer.Serialize(new
                {
                    batch_id = Guid.NewGuid().ToString("N"),
                    collector = db.CollectorCode(),
                    ingest = new[] { machinePayload }
                }));
            }

            var intervalSetting = db.GetSetting("simulator_interval");
            var seconds = int.TryParse(intervalSetting, out var parsed) ? Math.Max(1, parsed) : int.Parse(cfg["Collector:SimulatorIntervalSeconds"] ?? "5");
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }

    private static List<GeneratedValue> GenerateMachineValues(CollectorDb db, Machine machine, Random rnd)
    {
        var status = rnd.Next(0, 100) switch { < 70 => "running", < 82 => "idle", < 94 => "warning", _ => "fault" };
        var alarm = status is "warning" or "fault";
        var values = new List<GeneratedValue>();
        foreach (var tag in db.GetTagDefinitions(machine.Code).Where(t => t.Enabled && t.Simulation))
        {
            object value = tag.Role switch
            {
                "status" => status,
                "running" => status == "running",
                "alarm" => alarm,
                "count" => rnd.Next((int)(tag.MinValue ?? 1000), (int)(tag.MaxValue ?? 50000)),
                "speed" => RoundRandom(rnd, tag.MinValue ?? 40, tag.MaxValue ?? 120, 1),
                "oee" => RoundRandom(rnd, tag.MinValue ?? 65, tag.MaxValue ?? 98, 1),
                "temperature" => RoundRandom(rnd, tag.MinValue ?? 65, tag.MaxValue ?? 95, 1),
                _ => GenerateByType(tag, rnd)
            };
            values.Add(new GeneratedValue(tag.Tag, value, "GOOD", tag.DataType, tag.Role, tag.Unit));
        }
        return values;
    }

    private static object GenerateByType(TagDefinition tag, Random rnd)
    {
        return tag.DataType switch
        {
            "boolean" or "bool" => rnd.Next(0, 2) == 1,
            "integer" or "int" => rnd.Next((int)(tag.MinValue ?? 0), (int)(tag.MaxValue ?? 100)),
            "string" => "simulated",
            _ => RoundRandom(rnd, tag.MinValue ?? 0, tag.MaxValue ?? 100, 2)
        };
    }

    private static double RoundRandom(Random rnd, double min, double max, int decimals)
    {
        if (max <= min) max = min + 1;
        return Math.Round(min + rnd.NextDouble() * (max - min), decimals);
    }

    private record GeneratedValue(string Tag, object Value, string Quality, string DataType, string Role, string Unit);
}

public sealed class OdooPushWorker(IServiceProvider sp, IConfiguration cfg) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CollectorDb>();
            var client = scope.ServiceProvider.GetRequiredService<OdooClient>();
            if (db.GetSetting("odoo_enabled") == "true")
            {
                var item = db.NextQueue();
                if (item is not null)
                {
                    var (ok, err) = await client.PostRawAsync(item.Payload);
                    if (ok)
                    {
                        db.DeleteQueue(item.Id);
                    }
                    else
                    {
                        db.FailQueue(item.Id, err);
                    }
                }
            }

            var intervalSetting = db.GetSetting("odoo_push_interval");
            var seconds = int.TryParse(intervalSetting, out var parsed) ? Math.Max(1, parsed) : int.Parse(cfg["Collector:OdooPushIntervalSeconds"] ?? "10");
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }
}


public sealed class DriverWorker(IServiceProvider sp) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CollectorDb>();
            foreach (var driver in db.GetDrivers().Where(d => d.Enabled))
            {
                var result = await DriverTester.TestAsync(driver);
                db.SetDriverStatus(driver.Id, result.Ok ? "connected" : "error", result.Message, result.ElapsedMs);
            }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}

public static class DriverTester
{
    public static async Task<DriverTestResult> TestAsync(DriverDefinition driver)
    {
        var started = DateTime.UtcNow;
        try
        {
            if (driver.DriverType == "simulation")
            {
                await Task.Delay(5);
                return new DriverTestResult(true, "Simulation driver ready", (int)(DateTime.UtcNow - started).TotalMilliseconds);
            }
            if (driver.DriverType is "modbus_tcp" or "allen_bradley" or "mitsubishi_mc")
            {
                if (string.IsNullOrWhiteSpace(driver.Host) || driver.Port <= 0)
                    return new DriverTestResult(false, "Host or port is missing", 0);
                using var tcp = new System.Net.Sockets.TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await tcp.ConnectAsync(driver.Host, driver.Port, cts.Token);
                var message = driver.DriverType switch
                {
                    "modbus_tcp" => "TCP connected. Modbus read functions are ready for next driver phase.",
                    "allen_bradley" => "TCP connected to Ethernet/IP port. CIP browse/read implementation is next.",
                    "mitsubishi_mc" => "TCP connected to MC port. MC read implementation is next.",
                    _ => "TCP connected"
                };
                return new DriverTestResult(true, message, (int)(DateTime.UtcNow - started).TotalMilliseconds);
            }
            if (driver.DriverType is "opc_ua" or "mqtt" or "siemens_s7" or "kepware" or "fuxa")
            {
                return new DriverTestResult(false, $"{driver.DriverType} driver placeholder is configured but not implemented in Phase 3.", (int)(DateTime.UtcNow - started).TotalMilliseconds);
            }
            return new DriverTestResult(false, $"Unknown driver type: {driver.DriverType}", (int)(DateTime.UtcNow - started).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            return new DriverTestResult(false, ex.Message, (int)(DateTime.UtcNow - started).TotalMilliseconds);
        }
    }
}

public record DriverTestResult(bool Ok, string Message, int ElapsedMs);

public sealed class OdooClient(HttpClient http, CollectorDb db)
{
    public async Task<(bool ok, string message, string endpoint)> TestDetailedAsync()
    {
        var (ok, error, endpoint, statusCode, responseBody) = await GetHealthDetailedAsync();
        if (ok) return (true, $"Connection OK. Odoo health endpoint responded. HTTP {statusCode}. Response: {responseBody}", endpoint);
        var detail = string.IsNullOrWhiteSpace(responseBody) ? error : $"{error} Response: {responseBody}";
        return (false, detail, endpoint);
    }

    public async Task<(bool ok, string error, string endpoint, int statusCode, string responseBody)> GetHealthDetailedAsync()
    {
        return await SendAsync(HttpMethod.Get, "/plant_floor_monitor/api/v1/health", null);
    }

    public async Task<(bool ok, string error, string endpoint, int statusCode, string responseBody)> RegisterMachineAsync(string code, string name, bool simulation)
    {
        var payload = JsonSerializer.Serialize(new
        {
            batch_id = Guid.NewGuid().ToString("N"),
            collector = db.CollectorCode(),
            collector_name = db.GetSetting("collector_name"),
            machines = new[]
            {
                new
                {
                    collector = db.CollectorCode(),
                    collector_name = db.GetSetting("collector_name"),
                    machine_code = code,
                    machine_name = name,
                    protocol = simulation ? "simulator" : "manual",
                    simulation_enabled = simulation,
                    communication_status = "connected"
                }
            }
        });
        return await SendAsync(HttpMethod.Post, "/plant_floor_monitor/api/v1/batch", payload);
    }

    public async Task<(bool ok, string error, string endpoint, int statusCode, string responseBody)> SyncTagDefinitionsAsync()
    {
        return await PostBatchPayloadAsync(db.BuildTagDefinitionsBatchPayload());
    }

    public async Task<(bool ok, string error, string endpoint, int statusCode, string responseBody)> PostBatchPayloadAsync(string payload)
    {
        return await SendAsync(HttpMethod.Post, "/plant_floor_monitor/api/v1/batch", payload);
    }

    public async Task<(bool ok, string error, string endpoint, int statusCode, string responseBody, List<OdooMachine> machines)> GetMachinesFromOdooAsync()
    {
        var result = await SendAsync(HttpMethod.Get, "/plant_floor_monitor/api/v1/machines", null);
        if (!result.ok) return (false, result.error, result.endpoint, result.statusCode, result.responseBody, new List<OdooMachine>());
        var machines = new List<OdooMachine>();
        try
        {
            using var doc = JsonDocument.Parse(result.responseBody);
            if (doc.RootElement.TryGetProperty("machines", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var code = item.TryGetProperty("code", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? code : code;
                    var sim = item.TryGetProperty("simulation_enabled", out var se) && se.ValueKind == JsonValueKind.True;
                    if (!string.IsNullOrWhiteSpace(code)) machines.Add(new OdooMachine(code.Trim().ToUpperInvariant(), name, sim));
                }
            }
            return (true, string.Empty, result.endpoint, result.statusCode, result.responseBody, machines);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, result.endpoint, result.statusCode, result.responseBody, machines);
        }
    }

    public async Task<(bool ok, string error)> PostRawAsync(string payload)
    {
        var r = await SendAsync(HttpMethod.Post, "/plant_floor_monitor/api/v1/batch", payload);
        return (r.ok, r.error);
    }


    public async Task<(bool ok, string error, string endpoint, int statusCode, string responseBody)> CreateBackupAsync(string? backupName = null)
    {
        var name = string.IsNullOrWhiteSpace(backupName)
            ? $"{db.GetSetting("collector_name")} backup {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
            : backupName.Trim();
        var payload = JsonSerializer.Serialize(new
        {
            name = name,
            collector_code = db.CollectorCode(),
            collector_name = db.GetSetting("collector_name"),
            site_name = db.GetSetting("site_name"),
            config_json = db.ExportConfigurationJson(),
            machine_count = db.GetMachines().Count,
            tag_count = db.GetTagDefinitions().Count,
            driver_count = db.GetDrivers().Count
        });
        return await SendAsync(HttpMethod.Post, "/plant_floor_monitor/api/v1/config-backups", payload);
    }

    public async Task<(bool ok, string error, string endpoint, int statusCode, string responseBody, List<OdooBackupSummary> backups)> ListBackupsAsync()
    {
        var r = await SendAsync(HttpMethod.Get, "/plant_floor_monitor/api/v1/config-backups", null);
        var backups = new List<OdooBackupSummary>();
        if (r.ok)
        {
            try
            {
                using var doc = JsonDocument.Parse(r.responseBody);
                if (doc.RootElement.TryGetProperty("backups", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var b in arr.EnumerateArray())
                    {
                        backups.Add(new OdooBackupSummary(
                            b.TryGetProperty("id", out var id) && id.TryGetInt32(out var i) ? i : 0,
                            b.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                            b.TryGetProperty("collector_code", out var cc) ? cc.GetString() ?? "" : "",
                            b.TryGetProperty("created_at", out var ca) ? ca.GetString() ?? "" : "",
                            b.TryGetProperty("machine_count", out var mc) && mc.TryGetInt32(out var mci) ? mci : 0,
                            b.TryGetProperty("tag_count", out var tc) && tc.TryGetInt32(out var tci) ? tci : 0,
                            b.TryGetProperty("driver_count", out var dc) && dc.TryGetInt32(out var dci) ? dci : 0
                        ));
                    }
                }
            }
            catch (Exception ex)
            {
                return (false, ex.Message, r.endpoint, r.statusCode, r.responseBody, backups);
            }
        }
        return (r.ok, r.error, r.endpoint, r.statusCode, r.responseBody, backups);
    }

    public async Task<(bool ok, string error, string endpoint, int statusCode, string responseBody, string configJson)> GetBackupAsync(int id)
    {
        var r = await SendAsync(HttpMethod.Get, $"/plant_floor_monitor/api/v1/config-backups/{id}", null);
        var config = string.Empty;
        if (r.ok)
        {
            try
            {
                using var doc = JsonDocument.Parse(r.responseBody);
                if (doc.RootElement.TryGetProperty("config_json", out var cj)) config = cj.GetString() ?? "";
            }
            catch (Exception ex)
            {
                return (false, ex.Message, r.endpoint, r.statusCode, r.responseBody, config);
            }
        }
        return (r.ok, r.error, r.endpoint, r.statusCode, r.responseBody, config);
    }

    private async Task<(bool ok, string error, string endpoint, int statusCode, string responseBody)> SendAsync(HttpMethod method, string endpoint, string? payload)
    {
        try
        {
            var baseUrl = db.GetSetting("odoo_url").Trim();
            var token = db.GetSetting("odoo_token").Trim();
            if (string.IsNullOrWhiteSpace(baseUrl)) return (false, "Odoo URL is blank.", string.Empty, 0, string.Empty);
            if (!baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return (false, "Odoo URL must start with http:// or https://", baseUrl, 0, string.Empty);
            var url = baseUrl.TrimEnd('/') + "/" + endpoint.TrimStart('/');
            if (string.IsNullOrWhiteSpace(token)) return (false, "API Token is blank. Enter the same token from Odoo Plant Floor Monitor settings.", url, 0, string.Empty);

            using var req = new HttpRequestMessage(method, url);
            req.Headers.Add("X-Plant-Floor-Token", token);
            if (payload is not null)
            {
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var res = await http.SendAsync(req, cts.Token);
            var body = await res.Content.ReadAsStringAsync(cts.Token);
            return res.IsSuccessStatusCode
                ? (true, string.Empty, url, (int)res.StatusCode, body)
                : (false, $"HTTP {(int)res.StatusCode} {res.ReasonPhrase}", url, (int)res.StatusCode, body);
        }
        catch (TaskCanceledException)
        {
            return (false, "Connection timed out after 15 seconds.", db.GetSetting("odoo_url").TrimEnd('/') + endpoint, 0, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, db.GetSetting("odoo_url").TrimEnd('/') + endpoint, 0, string.Empty);
        }
    }
}

public sealed record UpdateManifest(string? Version, string? Image, string? Released, string[]? Notes);
public sealed record UpdateCheckResult(bool Ok, string Message, UpdateManifest? Manifest);
public sealed record UpdateRequest(DateTimeOffset RequestedAt, string CurrentVersion, string TargetVersion, string TargetImage, string DockerImageName, string CollectorName);

public static class Html
{
    public static string Layout(string title, string body) => $"""
<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'><title>{E(title)} - Plant Floor Collector</title><link rel='stylesheet' href='/style.css'></head><body><aside><h2>Plant Floor<br>Collector</h2><a href='/'>Dashboard</a><a href='/machines'>Machines</a><a href='/tags'>Live Tags</a><a href='/mappings'>Tag Mappings</a><a href='/drivers'>Drivers</a><a href='/diagnostics'>Diagnostics</a><a href='/odoo'>Odoo</a><a href='/backups'>Backups</a><a href='/updates'>Updates</a><a href='/settings'>Settings</a><a href='/about'>About</a><a href='/queue'>Queue</a><a href='/logs'>Logs</a></aside><main><h1>{E(title)}</h1>{body}</main></body></html>
""";

    public static string Dashboard(CollectorDb db)
    {
        return $"<div class='cards'><div class='card'><b>Machines</b><span>{db.GetMachines().Count}</span></div><div class='card'><b>Tag Definitions</b><span>{db.GetTagDefinitions().Count}</span></div><div class='card'><b>Live Tags</b><span>{db.LatestTags().Count}</span></div><div class='card'><b>Drivers</b><span>{db.GetDrivers().Count}</span></div><div class='card'><b>Queue</b><span>{db.QueueRows().Count}</span></div></div><h2>Machine Status</h2>" +
               string.Join("", db.GetMachines().Select(m => $"<div class='machine {E(m.Status)}'><b>{E(m.Code)}</b><small>{E(m.Name)}</small><span>{E(m.Status)}</span>{(m.Simulation ? "<em>SIMULATION</em>" : "")}</div>"));
    }

    public static string Machines(CollectorDb db)
    {
        var feedback = Feedback(db, "machine_sync_last");
        var rows = string.Join("", db.GetMachines().Select(m => $"<tr><td>{E(m.Code)}</td><td>{E(m.Name)}</td><td>{m.Enabled}</td><td>{m.Simulation}</td><td>{E(m.Status)}</td><td>{E(m.LastUpdate)}</td><td><a class='button small' href='/machines/edit/{Url(m.Code)}'>Edit</a></td></tr>"));
        return "<form method='post' class='panel'><input name='code' placeholder='Machine Code e.g. PACK01' required><input name='name' placeholder='Machine Name' required><label><input type='checkbox' name='enabled' checked> Enabled</label><label><input type='checkbox' name='sim' checked> Simulation</label><button>Add / Update Machine</button><p class='muted'>Saving creates default live tag definitions for the machine.</p></form>" +
               "<div class='panel actions'><form method='post' action='/machines/import-odoo'><button>Retrieve Machines from Odoo</button></form><form method='post' action='/machines/sync'><button>Sync All Machines to Odoo</button></form></div>" +
               feedback + "<table><tr><th>Code</th><th>Name</th><th>Enabled</th><th>Simulation</th><th>Status</th><th>Last Update</th><th>Actions</th></tr>" + rows + "</table>";
    }

    public static string EditMachine(Machine m) => $"<form method='post' action='/machines' class='panel'><label>Machine Code</label><input name='code' value='{E(m.Code)}' readonly><label>Machine Name</label><input name='name' value='{E(m.Name)}' required><label><input type='checkbox' name='enabled' {(m.Enabled ? "checked" : "")}> Enabled</label><label><input type='checkbox' name='sim' {(m.Simulation ? "checked" : "")}> Simulation</label><button>Save Machine</button><a class='button secondary' href='/machines'>Cancel</a></form>";

    public static string Tags(CollectorDb db)
    {
        var machines = db.GetMachines();
        var latest = db.LatestTagMap();
        var options = string.Join("", machines.Select(m => $"<option value='{E(m.Code)}'>{E(m.Code)} - {E(m.Name)}</option>"));
        var feedback = Feedback(db, "tag_sync_last");
        var rows = string.Join("", db.GetTagDefinitions().Select(t =>
        {
            latest.TryGetValue($"{t.MachineCode}.{t.Tag}", out var value);
            return $"<tr><td>{E(t.MachineCode)}</td><td>{E(t.Tag)}</td><td>{E(t.DataType)}</td><td>{E(t.Role)}</td><td>{E(t.Unit)}</td><td>{t.Enabled}</td><td>{t.Simulation}</td><td>{E(value?.Value ?? "")}</td><td>{E(value?.Quality ?? "")}</td><td>{E(value?.Ts ?? "")}</td><td><a class='button small' href='/tags/edit/{t.Id}'>Edit</a><form method='post' action='/tags/delete/{t.Id}' style='display:inline'><button class='danger small'>Delete</button></form></td></tr>";
        }));
        return $"<form method='post' class='panel gridform'><select name='machine'>{options}</select><input name='tag' placeholder='Tag Name e.g. MotorRunning' required><select name='data_type'><option>boolean</option><option>integer</option><option selected>float</option><option>string</option></select><input name='role' placeholder='Role e.g. running, count, speed, oee'><input name='unit' placeholder='Unit'><input name='min' placeholder='Sim Min'><input name='max' placeholder='Sim Max'><label><input type='checkbox' name='enabled' checked> Enabled</label><label><input type='checkbox' name='simulation' checked> Simulate</label><button>Add Tag</button></form><div class='panel actions'><form method='post' action='/tags/sync-odoo'><button>Sync Tag Definitions to Odoo</button></form><form method='post' action='/tags/push-values'><button>Push Current Values Now</button></form><p class='muted'>The simulator generates values for enabled tags marked Simulate. The background worker queues and pushes batches to Odoo.</p></div>{feedback}<table><tr><th>Machine</th><th>Tag</th><th>Type</th><th>Role</th><th>Unit</th><th>Enabled</th><th>Sim</th><th>Value</th><th>Quality</th><th>Time</th><th>Actions</th></tr>{rows}</table>";
    }

    public static string EditTag(CollectorDb db, TagDefinition t)
    {
        var options = string.Join("", db.GetMachines().Select(m => $"<option value='{E(m.Code)}' {(m.Code == t.MachineCode ? "selected" : "")}>{E(m.Code)} - {E(m.Name)}</option>"));
        string Select(string type) => type == t.DataType ? "selected" : string.Empty;
        return $"<form method='post' action='/tags' class='panel gridform'><input type='hidden' name='id' value='{t.Id}'><label>Machine</label><select name='machine'>{options}</select><label>Tag</label><input name='tag' value='{E(t.Tag)}' required><label>Data Type</label><select name='data_type'><option {Select("boolean")}>boolean</option><option {Select("integer")}>integer</option><option {Select("float")}>float</option><option {Select("string")}>string</option></select><label>Role</label><input name='role' value='{E(t.Role)}'><label>Unit</label><input name='unit' value='{E(t.Unit)}'><label>Simulation Min</label><input name='min' value='{E(t.MinValue?.ToString(CultureInfo.InvariantCulture) ?? "")}'><label>Simulation Max</label><input name='max' value='{E(t.MaxValue?.ToString(CultureInfo.InvariantCulture) ?? "")}'><label><input type='checkbox' name='enabled' {(t.Enabled ? "checked" : "")}> Enabled</label><label><input type='checkbox' name='simulation' {(t.Simulation ? "checked" : "")}> Simulate</label><button>Save Tag</button><a class='button secondary' href='/tags'>Cancel</a></form>";
    }



    public static string Mappings(CollectorDb db)
    {
        var drivers = db.GetDrivers();
        var feedback = Feedback(db, "mapping_last");
        string DriverOptions(long selected) => "<option value='0'>-- Select Driver --</option>" + string.Join("", drivers.Select(d => $"<option value='{d.Id}' {(d.Id == selected ? "selected" : "")}>{E(d.Name)} ({E(d.DriverType)})</option>"));
        string DirectionOptions(string selected)
        {
            string S(string v) => string.Equals(v, selected, StringComparison.OrdinalIgnoreCase) ? "selected" : "";
            return $"<option value='read' {S("read")}>Read</option><option value='write' {S("write")}>Write</option><option value='readwrite' {S("readwrite")}>Read/Write</option>";
        }
        var rows = string.Join("", db.GetTagDefinitions().Select(t => $"<tr><form method='post' action='/mappings'><input type='hidden' name='id' value='{t.Id}'><td>{E(t.MachineCode)}</td><td>{E(t.Tag)}</td><td>{E(t.DataType)}</td><td><select name='driver_id'>{DriverOptions(t.DriverId)}</select></td><td><input name='plc_address' value='{E(t.PlcAddress)}' placeholder='PLC tag/address'></td><td><select name='direction'>{DirectionOptions(t.Direction)}</select></td><td><input name='poll_rate_ms' type='number' min='0' value='{t.PollRateMs}'></td><td><input name='deadband' value='{E(t.Deadband?.ToString(CultureInfo.InvariantCulture) ?? "")}' placeholder='Optional'></td><td><label><input type='checkbox' name='write_enabled' {(t.WriteEnabled ? "checked" : "")}> Write</label></td><td><button class='small'>Save</button></td></form></tr>"));
        return $"<div class='panel actions'><a class='button' href='/mappings/export'>Export Mappings JSON</a><form method='post' action='/tags/sync-odoo'><button>Sync Tag Definitions to Odoo</button></form><form method='post' action='/tags/push-values'><button>Push Current Values Now</button></form></div>{feedback}<table><tr><th>Machine</th><th>Tag</th><th>Type</th><th>Driver</th><th>PLC Address / Tag Path</th><th>Direction</th><th>Poll ms</th><th>Deadband</th><th>Write</th><th>Action</th></tr>{rows}</table><div class='panel'><h2>Import Mappings JSON</h2><form method='post' action='/mappings/import'><textarea name='json' rows='8' placeholder='Paste exported mapping JSON here'></textarea><button>Import Mappings</button></form><p class='muted'>Phase 4 binds collector tags to driver instances and PLC addresses. Protocol-level browsing/import is staged after this mapping layer.</p></div>";
    }

    public static string Drivers(CollectorDb db)
    {
        var feedback = Feedback(db, "driver_last");
        var rows = string.Join("", db.GetDrivers().Select(d => $"<tr><td>{E(d.Name)}</td><td>{E(d.DriverType)}</td><td>{E(d.Host)}</td><td>{d.Port}</td><td>{E(d.Path)}</td><td>{d.ScanRateMs}</td><td>{d.Enabled}</td><td><span class='pill {E(d.Status)}'>{E(d.Status)}</span></td><td>{d.LastScanMs} ms</td><td>{E(d.LastError)}</td><td>{E(d.LastUpdate)}</td><td><a class='button small' href='/drivers/edit/{d.Id}'>Edit</a><form method='post' action='/drivers/test/{d.Id}' style='display:inline'><button class='small'>Test</button></form><form method='post' action='/drivers/delete/{d.Id}' style='display:inline'><button class='danger small'>Delete</button></form></td></tr>"));
        return DriverForm() + feedback + "<table><tr><th>Name</th><th>Type</th><th>Host</th><th>Port</th><th>Path / Options</th><th>Scan ms</th><th>Enabled</th><th>Status</th><th>Last Scan</th><th>Last Message</th><th>Updated</th><th>Actions</th></tr>" + rows + "</table><p class='muted'>Phase 3 adds the driver manager and connection diagnostics. Modbus TCP, Allen-Bradley, and Mitsubishi currently validate TCP connectivity; protocol-level reads are staged next.</p>";
    }

    private static string DriverForm() => "<form method='post' action='/drivers' class='panel gridform'><input name='name' placeholder='Driver Name' required><select name='driver_type'><option value='simulation'>Simulation</option><option value='modbus_tcp'>Modbus TCP</option><option value='allen_bradley'>Allen-Bradley Ethernet/IP</option><option value='mitsubishi_mc'>Mitsubishi MC</option><option value='opc_ua'>OPC UA</option><option value='mqtt'>MQTT</option><option value='siemens_s7'>Siemens S7</option><option value='kepware'>Kepware</option><option value='fuxa'>FUXA</option></select><input name='host' placeholder='Host / IP Address'><input name='port' type='number' placeholder='Port'><input name='path' placeholder='Path / Options e.g. 1,0 or UnitId=1'><input name='scan_rate' type='number' min='100' value='1000'><label><input type='checkbox' name='enabled' checked> Enabled</label><button>Add Driver</button></form>";

    public static string EditDriver(DriverDefinition d)
    {
        string S(string t) => d.DriverType == t ? "selected" : string.Empty;
        return $"<form method='post' action='/drivers' class='panel gridform'><input type='hidden' name='id' value='{d.Id}'><label>Name</label><input name='name' value='{E(d.Name)}' required><label>Type</label><select name='driver_type'><option value='simulation' {S("simulation")}>Simulation</option><option value='modbus_tcp' {S("modbus_tcp")}>Modbus TCP</option><option value='allen_bradley' {S("allen_bradley")}>Allen-Bradley Ethernet/IP</option><option value='mitsubishi_mc' {S("mitsubishi_mc")}>Mitsubishi MC</option><option value='opc_ua' {S("opc_ua")}>OPC UA</option><option value='mqtt' {S("mqtt")}>MQTT</option><option value='siemens_s7' {S("siemens_s7")}>Siemens S7</option><option value='kepware' {S("kepware")}>Kepware</option><option value='fuxa' {S("fuxa")}>FUXA</option></select><label>Host</label><input name='host' value='{E(d.Host)}'><label>Port</label><input name='port' type='number' value='{d.Port}'><label>Path / Options</label><input name='path' value='{E(d.Path)}'><label>Scan Rate ms</label><input name='scan_rate' type='number' min='100' value='{d.ScanRateMs}'><label><input type='checkbox' name='enabled' {(d.Enabled ? "checked" : "")}> Enabled</label><button>Save Driver</button><a class='button secondary' href='/drivers'>Cancel</a></form>";
    }

    public static string Diagnostics(CollectorDb db)
    {
        var driverRows = string.Join("", db.GetDrivers().Select(d => $"<tr><td>{E(d.Name)}</td><td>{E(d.DriverType)}</td><td>{E(d.Status)}</td><td>{d.LastScanMs} ms</td><td>{E(d.LastError)}</td><td>{E(d.LastUpdate)}</td></tr>"));
        var folderRows = string.Join("", db.FolderStatuses().Select(f => $"<tr><td>{E(f.Name)}</td><td><code>{E(f.Path)}</code></td><td>{f.Exists}</td><td>{f.Writable}</td><td>{E(f.Message)}</td></tr>"));
        return $"<div class='cards'><div class='card'><b>Version</b><span>{E(AppInfo.Version)}</span></div><div class='card'><b>Odoo Enabled</b><span>{E(db.GetSetting("odoo_enabled"))}</span></div><div class='card'><b>Queue Depth</b><span>{db.QueueRows().Count}</span></div><div class='card'><b>Live Tags</b><span>{db.LatestTags().Count}</span></div><div class='card'><b>Drivers</b><span>{db.GetDrivers().Count}</span></div></div><h2>Runtime Folders</h2><table><tr><th>Name</th><th>Path</th><th>Exists</th><th>Writable</th><th>Message</th></tr>{folderRows}</table><h2>Driver Diagnostics</h2><table><tr><th>Name</th><th>Type</th><th>Status</th><th>Latency</th><th>Last Message</th><th>Updated</th></tr>{driverRows}</table><h2>Recent Logs</h2>" + Logs(db);
    }



    public static string About()
    {
        return $"<div class='cards'><div class='card'><b>Version</b><span>{E(AppInfo.Version)}</span></div><div class='card'><b>Build Date</b><span>{E(AppInfo.BuildDate)}</span></div><div class='card'><b>Commit</b><span>{E(AppInfo.Commit)}</span></div></div>" +
               $"<div class='panel'><h2>Running Image</h2><p><code>{E(AppInfo.Image)}</code></p><p class='muted'>This page reads runtime build metadata from the running container/assembly, not from the local SQLite settings database.</p></div>" +
               $"<div class='panel'><h2>API</h2><p><a href='/api/about'>/api/about</a></p><p><a href='/api/status'>/api/status</a></p></div>";
    }

    public static string Updates(CollectorDb db)
    {
        var feedback = Feedback(db, "update_last");
        var current = E(AppInfo.Version);
        var latest = E(db.GetSetting("update_latest_version"));
        var image = E(db.GetSetting("update_latest_image"));
        var notes = E(db.GetSetting("update_latest_notes"));
        var available = db.GetSetting("update_available") == "true";
        var request = E(db.ReadUpdateRequest());
        var installDisabled = string.IsNullOrWhiteSpace(db.GetSetting("update_latest_image")) ? "disabled" : "";
        return $"<div class='cards'><div class='card'><b>Current Version</b><span>{current}</span></div><div class='card'><b>Latest Version</b><span>{latest}</span></div><div class='card'><b>Update Available</b><span>{available}</span></div></div>" +
               $"<form method='post' action='/updates/settings' class='panel gridform'><label>Update Manifest URL</label><input name='manifest_url' value='{E(db.GetSetting("update_manifest_url"))}' placeholder='https://server/version.json'><label>Docker Image Name</label><input name='docker_image' value='{E(db.GetSetting("docker_image_name"))}' placeholder='plant-floor-collector'><label><input type='checkbox' name='auto_backup' {(db.GetSetting("auto_backup_before_update") == "true" ? "checked" : "")}> Backup to Odoo before update</label><button>Save Update Settings</button></form>" +
               $"<div class='panel actions'><form method='post' action='/updates/check'><button>Check for Updates</button></form><form method='post' action='/updates/request' onsubmit=\"return confirm('Request update to {image}? The updater sidecar will restart the collector container.');\"><button {installDisabled}>Install Update</button></form></div>" +
               feedback +
               $"<div class='panel'><h2>Latest Release</h2><p><b>Image:</b> <code>{image}</code></p><pre>{notes}</pre></div>" +
               $"<div class='panel'><h2>Pending Update Request</h2><pre>{request}</pre><p class='muted'>The collector does not replace its own running container. It writes this request file into the persistent data volume. The updater sidecar reads it, pulls the image, recreates the collector container, and preserves the data volume.</p></div>";
    }

    public static string Backups(CollectorDb db, List<OdooBackupSummary> backups)
    {
        var feedback = Feedback(db, "backup_last");
        var rows = backups.Count == 0
            ? "<tr><td colspan='8' class='muted'>No backups returned from Odoo.</td></tr>"
            : string.Join("", backups.Select(b => $"<tr><td>{b.Id}</td><td>{E(b.Name)}</td><td>{E(b.CollectorCode)}</td><td>{E(b.CreatedAt)}</td><td>{b.MachineCount}</td><td>{b.TagCount}</td><td>{b.DriverCount}</td><td><form method='post' action='/backups/restore/{b.Id}' onsubmit=\"return confirm('Restore this backup into the local collector? This will update local machines, drivers, tags, mappings, and settings.');\"><button class='small'>Restore</button></form></td></tr>"));
        return $"<div class='panel'><form method='post' action='/backups/create' class='gridform'><label>Backup Name</label><input name='backup_name' placeholder='Main Plant Collector backup' value='{E(db.GetSetting("collector_name"))} backup {DateTime.Now:yyyy-MM-dd HH:mm:ss}' required><button>Save Current Configuration to Odoo</button></form></div><div class='panel actions'><a class='button secondary' href='/backups'>Refresh Available Backups</a><a class='button secondary' href='/backups/export-local'>Export Local JSON</a></div>{feedback}<table><tr><th>ID</th><th>Name</th><th>Collector</th><th>Created</th><th>Machines</th><th>Tags</th><th>Drivers</th><th>Action</th></tr>{rows}</table><p class='muted'>Backups are stored in Odoo and include collector settings, machines, drivers, tag definitions, and tag-to-driver mappings. API tokens are intentionally not restored.</p>";
    }

    public static string Odoo(CollectorDb db)
    {
        var feedback = Feedback(db, "odoo_last_test", connectionTitle: true);
        return $"<form method='post' class='panel'><label>Odoo URL</label><input name='url' value='{E(db.GetSetting("odoo_url"))}' placeholder='https://your-odoo.com'><label>Endpoint</label><input name='endpoint' value='{E(string.IsNullOrEmpty(db.GetSetting("odoo_endpoint")) ? "/plant_floor_monitor/api/v1/batch" : db.GetSetting("odoo_endpoint"))}'><label>API Token</label><input name='token' value='{E(db.GetSetting("odoo_token"))}'><label><input type='checkbox' name='enabled' {(db.GetSetting("odoo_enabled") == "true" ? "checked" : "")}> Enable Odoo Push</label><button>Save</button></form><form method='post' action='/odoo/test' class='panel'><button>Test Connection</button></form>{feedback}<p>Preferred endpoint: <code>/plant_floor_monitor/api/v1/batch</code></p>";
    }

    public static string Settings(CollectorDb db)
    {
        var folderRows = string.Join("", db.FolderStatuses().Select(f => $"<tr><td>{E(f.Name)}</td><td><code>{E(f.Path)}</code></td><td>{f.Exists}</td><td>{f.Writable}</td><td>{E(f.Message)}</td></tr>"));
        return $"<form method='post' class='panel'><label>Collector Name</label><input name='collector_name' value='{E(db.GetSetting("collector_name"))}'><label>Site Name</label><input name='site_name' value='{E(db.GetSetting("site_name"))}'><label>Simulator Interval Seconds</label><input name='simulator_interval' type='number' min='1' value='{E(db.GetSetting("simulator_interval"))}'><label>Odoo Push Interval Seconds</label><input name='odoo_push_interval' type='number' min='1' value='{E(db.GetSetting("odoo_push_interval"))}'><label>Log Retention Days</label><input name='retention_days' type='number' min='1' value='{E(db.GetSetting("retention_days"))}'><button>Save Settings</button></form><div class='panel'><h2>Runtime Folder / Volume Check</h2><p class='muted'>These folders are created automatically at startup. In Docker, each should be mounted to a named volume for production deployments.</p><table><tr><th>Name</th><th>Path</th><th>Exists</th><th>Writable</th><th>Message</th></tr>{folderRows}</table></div>";
    }

    public static string Queue(CollectorDb db) => "<table><tr><th>ID</th><th>Created</th><th>Attempts</th><th>Last Error</th><th>Payload</th></tr>" + string.Join("", db.QueueRows().Select(r => $"<tr><td>{E(r[0])}</td><td>{E(r[1])}</td><td>{E(r[2])}</td><td>{E(r[3])}</td><td><code>{E(r[4])}</code></td></tr>")) + "</table>";
    public static string Logs(CollectorDb db) => "<table><tr><th>Time</th><th>Level</th><th>Message</th></tr>" + string.Join("", db.Logs().Select(r => $"<tr><td>{E(r[0])}</td><td>{E(r[1])}</td><td>{E(r[2])}</td></tr>")) + "</table>";

    private static string Feedback(CollectorDb db, string prefix, bool connectionTitle = false)
    {
        var ok = db.GetSetting(prefix + "_ok");
        var msg = E(db.GetSetting(prefix + "_message"));
        var time = E(db.GetSetting(prefix + "_time"));
        var endpoint = E(db.GetSetting(prefix + "_endpoint"));
        if (string.IsNullOrEmpty(time)) return string.Empty;
        var title = connectionTitle ? (ok == "true" ? "Connection Test Passed" : "Connection Test Failed") : "Last Result";
        var endpointHtml = string.IsNullOrWhiteSpace(endpoint) ? string.Empty : $"<p><b>Endpoint:</b> <code>{endpoint}</code></p>";
        return $"<div class='alert {(ok == "true" ? "ok" : "bad")}'><h2>{title}</h2><p><b>Time:</b> {time}</p>{endpointHtml}<pre>{msg}</pre></div>";
    }

    private static string E(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
    private static string Url(string value) => System.Net.WebUtility.UrlEncode(value);
}
