using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Magicians.Native;

namespace Magicians.Tooling;

/// <summary>Separates immutable historical evidence from the active C# implementation guard.</summary>
internal static class FrozenIntegrity
{
    private const string NativePath = "releases/v1.2/native-source-manifest.json";
    private const string RetiredPath = "releases/v1.2/retired-python-manifest.json";
    private const string LegacyPath = "releases/v1.2/freeze-manifest.json";
    private const string ReferenceCommit = "2e38abc44a37214b4edd0aad6aa2ab273e171c97";
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".csproj", ".ps1", ".sln", ".cfg", ".godot", ".config", ".uid", ".tscn", ".svg", ".import" };
    private static string HashMode(string path) => TextExtensions.Contains(Path.GetExtension(path)) || path.EndsWith(".cs.txt", StringComparison.OrdinalIgnoreCase) ? "utf8-lf" : "raw-sha256";
    private static string Hash(string path, string mode)
    {
        if (mode == "raw-sha256") return DataJson.Sha(path);
        DataJson.Require(mode == "utf8-lf", "Unknown source hash mode: " + mode);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataJson.Normalize(File.ReadAllText(path))))).ToLowerInvariant();
    }
    private static IEnumerable<string> NativeFiles(string root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in new[] { "Scripts", "Scenes", "Assets", "Data", "tools", "docs/cards-v1.2", "releases/v1.2", "releases/ai-v1.2-six-styles-20261029" })
        {
            var folder = Path.Combine(root, dir);
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var relative = DataJson.Relative(root, file);
                var parts = relative.Split('/');
                if (parts.Any(p => p is "bin" or "obj" or "__pycache__" or ".godot" or "runtime")) continue;
                if (relative == NativePath || Path.GetExtension(file).Equals(".py", StringComparison.OrdinalIgnoreCase)) continue;
                if (TextExtensions.Contains(Path.GetExtension(file)) || new[] { ".json", ".md", ".gz", ".txt", ".png", ".jpg", ".ogg" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) names.Add(relative);
            }
        }
        foreach (var name in new[] { "Magicians!.csproj", "Magicians!.sln", "NuGet.config", "project.godot", "export_presets.cfg" })
            if (File.Exists(Path.Combine(root, name))) names.Add(name);
        var tooling = Path.Combine(root, "tools");
        if (Directory.Exists(tooling)) foreach (var file in Directory.GetFiles(tooling, "*.ps1")) names.Add(DataJson.Relative(root, file));
        var migration = Path.Combine(root, "migration");
        if (Directory.Exists(migration))
            foreach (var file in Directory.EnumerateFiles(migration, "*", SearchOption.AllDirectories))
            {
                var relative = DataJson.Relative(root, file);
                if (relative.Split('/').Any(p => p is "bin" or "obj" or "runtime" or ".dotnet-home" or "dotnet-home")) continue;
                if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".cs.txt", StringComparison.OrdinalIgnoreCase)) names.Add(relative);
            }
        return names.Order(StringComparer.Ordinal);
    }
    internal static void Freeze(string root, string[] args)
    {
        var path = Path.Combine(root, NativePath);
        if (args.Contains("--capture-retired-sources"))
        {
            DataJson.Require(!args.Contains("--create-native-manifest") && !args.Contains("--refresh-native-manifest"), "Capture retirement evidence separately from source sealing");
            var history = Historical(root);
            var retired = Path.Combine(root, RetiredPath);
            if (!File.Exists(retired)) DataJson.Write(retired, CaptureRetiredSources(root));
            ValidateRetired(root, history.Legacy, history.Archive);
            Console.WriteLine("V12_RETIRED_SOURCES_CAPTURED reference_only=true");
            return;
        }
        var create = args.Contains("--create-native-manifest");
        var refresh = args.Contains("--refresh-native-manifest");
        if (!create && !refresh) { Verify(root, args); return; }
        if (create && File.Exists(path)) throw new InvalidOperationException("Native manifest already exists; use verify, or explicitly --refresh-native-manifest for an authorized implementation change.");
        DataJson.Require(!create || !refresh, "Choose create or refresh, not both");
        RequireNoActivePython(root);
        // Always validate frozen rules/evidence first. Explicit source sealing cannot authorize a rules change.
        var evidence = Historical(root);
        Catalog.validate_sources(); DataCommands.StaticAudit(root);
        var retirementPath = Path.Combine(root, RetiredPath);
        if (!File.Exists(retirementPath)) DataJson.Write(retirementPath, CaptureRetiredSources(root));
        ValidateRetired(root, evidence.Legacy, evidence.Archive);
        var files = new JsonObject();
        foreach (var name in NativeFiles(root))
        {
            var mode = HashMode(name);
            files[name] = new JsonObject { ["sha256"] = Hash(Path.Combine(root, name), mode), ["hash_mode"] = mode };
        }
        DataJson.Require(files.KeysAny("Scripts/Native/") && files.KeysAny("tools/Magicians.Tooling/"), "Native runtime or tooling missing");
        var result = new JsonObject
        {
            ["schema_version"] = 1, ["version"] = "1.2", ["status"] = "frozen", ["implementation"] = "native-csharp-runtime-and-tooling",
            ["created_utc"] = DateTimeOffset.UtcNow.ToString("O"), ["historical_manifest"] = LegacyPath, ["historical_manifest_sha256"] = DataJson.Sha(Path.Combine(root, LegacyPath)),
            ["retired_python_manifest"] = RetiredPath, ["retired_python_manifest_sha256"] = DataJson.Sha(retirementPath),
            ["boundary"] = "Historical Python sources are retired to the referenced Git commit; current C# implementation and rule/data inputs are strictly verified by this manifest. A missing retired source is a historical reference, never a successful live-source hash check.",
            ["text_hash_policy"] = "UTF-8 source text is hashed after CRLF to LF normalization; JSON/Markdown/models and historical archive use exact raw bytes.",
            ["cards"] = 183, ["pool_copies"] = 677, ["files"] = files
        };
        DataJson.Write(path, result);
        Console.WriteLine("V12_NATIVE_SOURCE_FROZEN files=" + files.Count);
        Verify(root, args);
    }
    private sealed record HistoricalResult(JsonObject Legacy, string Archive, int PackageEntries, int CurrentDataFiles);
    private static HistoricalResult Historical(string root)
    {
        var legacy = DataJson.Read(Path.Combine(root, LegacyPath));
        DataJson.Require(legacy["status"]!.GetValue<string>() == "frozen", "Legacy freeze status changed");
        var archive = DataJson.RootPath(root, legacy["package"]!.GetValue<string>());
        DataJson.Require(File.Exists(archive) && DataJson.Sha(archive) == legacy["package_sha256"]!.GetValue<string>(), "Historical frozen ZIP checksum mismatch");
        using var zip = ZipFile.OpenRead(archive);
        var protectedEntries = legacy["package_files_sha256"]!.AsObject();
        DataJson.Require(zip.Entries.Count == protectedEntries.Count, "Historical ZIP entries added or removed");
        foreach (var (name, expected) in protectedEntries)
        {
            var entry = zip.GetEntry(name) ?? throw new InvalidDataException("Historical ZIP entry missing: " + name);
            using var stream = entry.Open(); DataJson.Require(DataJson.Sha(stream) == expected!.GetValue<string>(), "Historical ZIP entry changed: " + name);
        }
        var liveData = 0;
        foreach (var (name, expected) in legacy["sha256"]!.AsObject())
        {
            if (name.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) continue; // Explicitly checked by ValidateRetired, not counted as current files.
            var path = DataJson.RootPath(root, name);
            DataJson.Require(File.Exists(path) && DataJson.Sha(path) == expected!.GetValue<string>(), "Frozen v1.2 data changed: " + name);
            liveData++;
        }
        return new(legacy, archive, zip.Entries.Count, liveData);
    }
    private static JsonObject CaptureRetiredSources(string root)
    {
        var old = DataJson.Read(Path.Combine(root, LegacyPath));
        using var archive = ZipFile.OpenRead(DataJson.RootPath(root, old["package"]!.GetValue<string>()));
        var files = new JsonObject();
        foreach (var file in Directory.EnumerateFiles(root, "*.py", SearchOption.AllDirectories).Where(p => !DataJson.Relative(root, p).Split('/').Any(part => part is ".git" or "runtime" or "bin" or "obj")).Order(StringComparer.Ordinal))
        {
            var name = DataJson.Relative(root, file); var raw = File.ReadAllBytes(file);
            var sha = DataJson.Sha(file); var expected = old["sha256"]?[name]?.GetValue<string>();
            var entry = archive.GetEntry(name); string? zipHash = null;
            if (entry is not null) { using var stream = entry.Open(); zipHash = DataJson.Sha(stream); }
            var prefix = Encoding.UTF8.GetBytes("blob " + raw.Length + "\0");
            files[name] = new JsonObject
            {
                ["sha256_at_retirement"] = sha,
                ["git_blob_sha1"] = Convert.ToHexString(SHA1.HashData(prefix.Concat(raw).ToArray())).ToLowerInvariant(),
                ["git_commit"] = ReferenceCommit, ["old_freeze_expected_sha256"] = expected,
                ["historical_zip_sha256"] = zipHash, ["zip_matches_retired_source"] = zipHash is null ? null : zipHash == sha,
                ["old_freeze_matches_retired_source"] = expected is null ? null : expected == sha
            };
        }
        foreach (var name in old["sha256"]!.AsObject().Select(r => r.Key).Where(n => n.EndsWith(".py", StringComparison.OrdinalIgnoreCase)))
            DataJson.Require(files.ContainsKey(name), "Create retirement manifest before removing protected Python: " + name);
        return new JsonObject { ["schema_version"] = 1, ["version"] = "1.2", ["status"] = "retired-reference", ["git_repository"] = "sjtu-Mutsumi/Magicians-drifts-", ["git_commit"] = ReferenceCommit,
            ["original_collaborator_commit"] = "0c0150dbeda044f06c0e31d02a157aa56c7106c8", ["note"] = "Historical evidence only. Missing source is not verified offline; current executable C# is protected by native-source-manifest.json.", ["files"] = files };
    }
    private static JsonObject ValidateRetired(string root, JsonObject legacy, string archivePath)
    {
        var path = Path.Combine(root, RetiredPath);
        DataJson.Require(File.Exists(path), "Retired Python reference manifest missing; create native freeze before deleting original tools");
        var retired = DataJson.Read(path); var files = retired["files"]!.AsObject();
        using var archive = ZipFile.OpenRead(archivePath);
        var present = 0; var missing = 0; var archiveMatches = 0;
        foreach (var (name, row) in files)
        {
            DataJson.Require(name.EndsWith(".py", StringComparison.OrdinalIgnoreCase) && row!["git_commit"]!.GetValue<string>() == ReferenceCommit, "Invalid retired source reference: " + name);
            var file = DataJson.RootPath(root, name);
            if (File.Exists(file)) { DataJson.Require(DataJson.Sha(file) == row!["sha256_at_retirement"]!.GetValue<string>(), "Retired Python source changed before removal: " + name); present++; }
            else missing++;
            var expected = legacy["sha256"]?[name]?.GetValue<string>();
            DataJson.Require(row!["old_freeze_expected_sha256"]?.GetValue<string>() == expected, "Retired source old-freeze reference changed: " + name);
            var entry = archive.GetEntry(name); string? zipHash = null;
            if (entry is not null) { using var stream = entry.Open(); zipHash = DataJson.Sha(stream); }
            DataJson.Require(row["historical_zip_sha256"]?.GetValue<string>() == zipHash, "Retired source ZIP reference changed: " + name);
            if (zipHash == row["sha256_at_retirement"]!.GetValue<string>()) archiveMatches++;
        }
        foreach (var row in legacy["sha256"]!.AsObject().Where(row => row.Key.EndsWith(".py", StringComparison.OrdinalIgnoreCase)))
            DataJson.Require(files.ContainsKey(row.Key), "Protected retired source reference omitted: " + row.Key);
        return new JsonObject { ["status"] = "historical-reference", ["references"] = files.Count, ["present_source_hashes_verified"] = present, ["absent_sources_not_offline_verified"] = missing, ["sources_also_matching_historical_zip"] = archiveMatches, ["git_commit"] = ReferenceCommit };
    }
    internal static void Verify(string root, string[] args)
    {
        RequireNoActivePython(root);
        var nativePath = Path.Combine(root, NativePath);
        DataJson.Require(File.Exists(nativePath), "Native implementation manifest missing. Review source, then explicitly run freeze --create-native-manifest.");
        var native = DataJson.Read(nativePath);
        DataJson.Require(native["status"]!.GetValue<string>() == "frozen" && native["implementation"]!.GetValue<string>() == "native-csharp-runtime-and-tooling", "Invalid native implementation manifest");
        DataJson.Require(native["historical_manifest_sha256"]!.GetValue<string>() == DataJson.Sha(Path.Combine(root, LegacyPath)), "Historical manifest changed after migration sealing");
        DataJson.Require(native["retired_python_manifest_sha256"]!.GetValue<string>() == DataJson.Sha(Path.Combine(root, RetiredPath)), "Retired source reference changed after migration sealing");
        var history = Historical(root);
        var references = ValidateRetired(root, history.Legacy, history.Archive);
        var expectedFiles = native["files"]!.AsObject();
        DataJson.Require(NativeFiles(root).ToHashSet(StringComparer.Ordinal).SetEquals(expectedFiles.Select(row => row.Key)), "Current native source/data set changed: added or removed protected files");
        foreach (var (name, value) in expectedFiles)
        {
            var path = DataJson.RootPath(root, name);
            DataJson.Require(File.Exists(path) && Hash(path, value!["hash_mode"]!.GetValue<string>()) == value["sha256"]!.GetValue<string>(), "Current native implementation changed: " + name);
        }
        Catalog.validate_sources();
        DataJson.Require(Catalog.CARDS.Count == 183 && Catalog.CARDS.Values.Sum(Catalog.copies) == 677, "Card inventory changed");
        foreach (var style in SixStyles.IDS) _ = SixStyles.load_policy(style);
        var contracts = DataJson.Read(Path.Combine(root, "docs/cards-v1.2/snapshot-contract.json"))["records"]!.AsObject();
        DataJson.Require(contracts.Select(row => row.Key).ToHashSet(StringComparer.Ordinal).SetEquals(Catalog.CARDS.Keys), "Layer contract coverage changed");
        foreach (var (name, card) in Catalog.CARDS)
        {
            var value = PanelBudgets.Value(card.cost, card.attack, card.defense); var (low, high) = PanelBudgets.Bounds(name, card.rarity);
            DataJson.Require(value >= low && value <= high && contracts[name]!["card_text"]!.GetValue<string>() == card.text, "Card budget or contract changed: " + name);
        }
        DataJson.Require(JsonNode.DeepEquals(PresentationNode(root), DataJson.Read(Path.Combine(root, "Data/Presentation/card-display.json"))), "Approved display data stale");
        var result = new JsonObject { ["status"] = "passed", ["runtime"] = "native-csharp", ["cards"] = 183, ["stock"] = 677, ["models"] = 6,
            ["historical_zip_integrity"] = new JsonObject { ["status"] = "passed", ["entries"] = history.PackageEntries, ["current_frozen_data_files"] = history.CurrentDataFiles },
            ["retired_source_reference"] = references,
            ["current_native_integrity"] = new JsonObject { ["status"] = "passed", ["files"] = expectedFiles.Count, ["added_removed_or_changed_source_fails"] = true } };
        DataJson.Write(DataJson.Destination(root, args, "runtime/csharp-tooling/verification.json"), result);
        Console.WriteLine("V12_RELEASE_VERIFIED cards=183 stock=677 historical_zip=true retired_sources=reference current_native=true");
    }
    private static JsonObject PresentationNode(string root) => DataCommands.Presentation(root);
    private static bool KeysAny(this JsonObject obj, string prefix) => obj.Any(row => row.Key.StartsWith(prefix, StringComparison.Ordinal));
    private static void RequireNoActivePython(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var name = DataJson.Relative(root, path);
            if (name.Split('/').Any(part => part is ".git" or "runtime" or "bin" or "obj" or "__pycache__" or ".godot" or ".dotnet-home" or "dotnet-home" or ".nuget")) continue;
            var extension = Path.GetExtension(path).ToLowerInvariant(); var file = Path.GetFileName(path);
            var pythonBinary = file.StartsWith("python", StringComparison.OrdinalIgnoreCase) && extension is ".exe" or ".dll";
            if (extension is ".py" or ".pyw" or ".pyc" or ".pyo" or ".pyd" or ".pyz" || pythonBinary)
                throw new InvalidDataException("Active Python file is forbidden in the completed C# project: " + name);
        }
    }

    internal static void DeployPolicy(string root, JsonObject candidate, string expectedPolicySha256, bool heldoutGatePassed)
    {
        DataJson.Require(heldoutGatePassed, "Candidate did not pass the required final heldout promotion gate");
        DataJson.Require(candidate["weights"] is JsonObject && candidate["checkpoint"] is JsonValue && !string.IsNullOrWhiteSpace(candidate["checkpoint"]!.GetValue<string>()), "Invalid candidate policy or checkpoint");
        static void FiniteValues(JsonNode? node)
        {
            if (node is JsonObject obj) foreach (var row in obj) FiniteValues(row.Value);
            else if (node is JsonArray array) foreach (var value in array) FiniteValues(value);
            else if (node is JsonValue value && value.TryGetValue<double>(out var number))
                DataJson.Require(double.IsFinite(number), "Candidate policy contains a non-finite number");
        }
        FiniteValues(candidate);
        const string policyName = "tools/sim_v12/rules/ai-policy.json";
        var policyPath = Path.Combine(root, policyName);
        var manifestPath = Path.Combine(root, NativePath);
        // Verification is read-only with respect to protected files. No deployment is allowed from a dirty tree.
        Verify(root, []);
        DataJson.Require(DataJson.Sha(policyPath) == expectedPolicySha256, "Default policy changed since training began");
        var originalPolicy = File.ReadAllBytes(policyPath); var originalManifest = File.ReadAllBytes(manifestPath);
        var manifest = DataJson.Read(manifestPath);
        var entry = manifest["files"]![policyName]!.AsObject();
        DataJson.Require(entry["hash_mode"]!.GetValue<string>() == "raw-sha256" && entry["sha256"]!.GetValue<string>() == expectedPolicySha256, "Default policy fingerprint does not match training input");
        var newline = Encoding.UTF8.GetString(originalPolicy).Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var newPolicy = Encoding.UTF8.GetBytes(DataJson.Text(candidate).Replace("\n", newline));
        var newHash = Convert.ToHexString(SHA256.HashData(newPolicy)).ToLowerInvariant();
        entry["sha256"] = newHash;
        var manifestNewline = Encoding.UTF8.GetString(originalManifest).Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var newManifest = Encoding.UTF8.GetBytes(DataJson.Text(manifest).Replace("\n", manifestNewline));
        var auditFolder = Path.Combine(root, "runtime", "csharp-tooling", "deployments", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(auditFolder);
        File.WriteAllBytes(Path.Combine(auditFolder, "original-ai-policy.json"), originalPolicy);
        File.WriteAllBytes(Path.Combine(auditFolder, "original-native-source-manifest.json"), originalManifest);
        var audit = new JsonObject { ["status"] = "prepared", ["explicit_deployment"] = true, ["heldout_gate_passed"] = true,
            ["checkpoint"] = candidate["checkpoint"]!.GetValue<string>(), ["policy"] = policyName, ["before_sha256"] = expectedPolicySha256,
            ["after_sha256"] = newHash, ["changed_fingerprints"] = new JsonArray(policyName), ["six_style_bundle_changed"] = false };
        var auditPath = Path.Combine(auditFolder, "deployment.json"); DataJson.Write(auditPath, audit);
        // Detect a concurrent edit between verification and committing either protected file.
        DataJson.Require(File.ReadAllBytes(policyPath).SequenceEqual(originalPolicy) && File.ReadAllBytes(manifestPath).SequenceEqual(originalManifest), "Source or policy changed concurrently before deployment");
        try
        {
            AtomicWrite(policyPath, newPolicy); AtomicWrite(manifestPath, newManifest);
            Verify(root, []);
            audit["status"] = "deployed-and-verified"; DataJson.Write(auditPath, audit);
        }
        catch (Exception error)
        {
            try
            {
                AtomicWrite(policyPath, originalPolicy); AtomicWrite(manifestPath, originalManifest);
                audit["status"] = "rolled-back"; audit["error"] = error.Message; DataJson.Write(auditPath, audit);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Policy deployment failed and rollback also failed; original bytes remain in the runtime deployment backup.", error, rollbackError);
            }
            throw;
        }
    }
    private static void AtomicWrite(string destination, byte[] bytes)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
