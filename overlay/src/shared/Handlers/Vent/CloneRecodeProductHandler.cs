#if INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_clone_recode_product</c> creates an isolated, renamed product tree while preserving Inventor
/// document ancestry. Native documents are written with SaveCopyAs, their copied parents are rebound with
/// FileDescriptor.ReplaceReference, and source references/iProperties are restored before returning.
///
/// This is the in-Inventor equivalent of the factory's Pack-and-Go + manual recoding workflow. It deliberately
/// excludes references outside source_root (for example shared Content Center files), refuses collisions, has a
/// bounded file count, defaults to dry-run, and leaves an INCOMPLETE manifest if a filesystem-stage fails.
/// </summary>
public sealed class CloneRecodeProductHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_clone_recode_product";
    public bool IsReadOnly => false;

    private const int MaxFiles = 512;
    private static readonly HashSet<string> NativeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ipt", ".iam", ".idw", ".dwg", ".ipn"
    };

    private sealed class CloneState
    {
        public Application App { get; set; } = null!;
        public string SourceRoot { get; set; } = "";
        public string DestinationRoot { get; set; } = "";
        public string SourceCode { get; set; } = "";
        public string TargetCode { get; set; } = "";
        public bool UpdateIProperties { get; set; }
        public Dictionary<string, string> FileMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Completed = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Visiting = new(StringComparer.OrdinalIgnoreCase);
        public readonly JArray Rebound = new();
        public readonly JArray ExternalReferences = new();
        public readonly JArray Warnings = new();
        public readonly List<global::Inventor.Document> OpenedByHandler = new();
    }

    private sealed class PropertySnapshot
    {
        public Inventor.Property Property { get; set; } = null!;
        public object Value { get; set; } = "";
    }

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        string sourceRoot = FullPath(p["source_root"]?.Value<string>() ?? "");
        string destinationRoot = FullPath(p["destination_root"]?.Value<string>() ?? "");
        string topPath = FullPath(p["top_document"]?.Value<string>() ?? "");
        string sourceCode = (p["source_code"]?.Value<string>() ?? "").Trim();
        string targetCode = (p["target_code"]?.Value<string>() ?? "").Trim();
        bool updateIProperties = p["update_iproperties"]?.Value<bool?>() ?? true;
        bool dryRun = p["dry_run"]?.Value<bool?>() ?? true;

        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "source_root does not exist");
        if (string.IsNullOrWhiteSpace(topPath) || !File.Exists(topPath) || !IsUnder(topPath, sourceRoot))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "top_document must exist under source_root");
        if (!NativeExtensions.Contains(Path.GetExtension(topPath)))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "top_document must be an Inventor native document");
        if (string.IsNullOrWhiteSpace(sourceCode) || string.IsNullOrWhiteSpace(targetCode) ||
            string.Equals(sourceCode, targetCode, StringComparison.OrdinalIgnoreCase))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                "source_code and target_code are required and must differ");
        if (sourceCode.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            targetCode.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            sourceCode.IndexOf(Path.DirectorySeparatorChar) >= 0 || sourceCode.IndexOf(Path.AltDirectorySeparatorChar) >= 0 ||
            targetCode.IndexOf(Path.DirectorySeparatorChar) >= 0 || targetCode.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                "source_code and target_code must be safe filename fragments");
        if (string.IsNullOrWhiteSpace(destinationRoot) || Directory.Exists(destinationRoot))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                "destination_root must be a new absolute product directory");
        if (IsUnder(destinationRoot, sourceRoot) || IsUnder(sourceRoot, destinationRoot))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                "source_root and destination_root must not contain one another");
        if (ExportPathPolicy.TryRejectPath(Path.Combine(destinationRoot, "clone-manifest.json"), out var rejection))
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, rejection);

        // SaveCopyAs works on the in-memory document. Record dirty sources in dry-run evidence and refuse
        // execution until the engineer has explicitly saved or discarded those edits.
        var dirtySources = new JArray();
        foreach (global::Inventor.Document open in app.Documents)
        {
            try
            {
                string openPath = FullPath(open.FullFileName);
                if (IsUnder(openPath, sourceRoot) && open.Dirty)
                    dirtySources.Add(openPath);
            }
            catch { }
        }

        string[] sourceFiles;
        try { sourceFiles = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories); }
        catch (Exception ex) { return Fail(ctx, InventorErrorCodes.API_ERROR, "cannot enumerate source tree: " + ex.Message); }
        if (sourceFiles.Length == 0 || sourceFiles.Length > MaxFiles)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                $"source tree must contain 1..{MaxFiles} files; found {sourceFiles.Length}");

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var targetSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in sourceFiles)
        {
            string relative = RelativeUnder(source, sourceRoot);
            string renamedRelative = ReplaceOrdinalIgnoreCase(relative, sourceCode, targetCode);
            string target = FullPath(Path.Combine(destinationRoot, renamedRelative));
            if (!targetSet.Add(target))
                return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                    "recoding creates a destination collision: " + target);
            map[FullPath(source)] = target;
        }

        string mappedTop = map[topPath];
        var planned = new JArray(map.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new JObject
            {
                ["source"] = kv.Key,
                ["destination"] = kv.Value,
                ["native"] = NativeExtensions.Contains(Path.GetExtension(kv.Key)),
            }));
        var preflight = new JObject
        {
            ["dry_run"] = dryRun,
            ["source_root"] = sourceRoot,
            ["destination_root"] = destinationRoot,
            ["source_code"] = sourceCode,
            ["target_code"] = targetCode,
            ["top_document"] = mappedTop,
            ["file_count"] = sourceFiles.Length,
            ["renamed_count"] = map.Count(kv => !string.Equals(
                RelativeUnder(kv.Key, sourceRoot), RelativeUnder(kv.Value, destinationRoot), StringComparison.Ordinal)),
            ["pass"] = dirtySources.Count == 0,
            ["source_dirty_documents"] = dirtySources,
            ["files"] = planned,
        };
        if (dryRun) return Ok(ctx, preflight);
        if (dirtySources.Count > 0)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT,
                "save or discard source changes before clone/recode: " + string.Join(", ", dirtySources.Values<string>()));

        var state = new CloneState
        {
            App = app,
            SourceRoot = sourceRoot,
            DestinationRoot = destinationRoot,
            SourceCode = sourceCode,
            TargetCode = targetCode,
            UpdateIProperties = updateIProperties,
            FileMap = map,
        };

        string incompletePath = Path.Combine(destinationRoot, "INCOMPLETE.json");
        try
        {
            Directory.CreateDirectory(destinationRoot);
            File.WriteAllText(incompletePath, new JObject
            {
                ["state"] = "running",
                ["started_at_utc"] = DateTime.UtcNow.ToString("O"),
                ["preflight"] = preflight,
            }.ToString());

            // Copy ancillary files first. Referenced spreadsheets/images can then be rebound while native
            // parents are saved. Native documents are always written through Inventor SaveCopyAs.
            foreach (var kv in map)
            {
                if (NativeExtensions.Contains(Path.GetExtension(kv.Key))) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(kv.Value)!);
                File.Copy(kv.Key, kv.Value, false);
                state.Completed.Add(kv.Key);
            }

            global::Inventor.Document top = FindOrOpen(state, topPath, false);
            CloneDocument(top, state);

            // Drawings and unreferenced library parts are not reachable from the top model, so copy them too.
            foreach (var kv in map.Where(kv => NativeExtensions.Contains(Path.GetExtension(kv.Key))))
            {
                if (state.Completed.Contains(kv.Key)) continue;
                global::Inventor.Document doc = FindOrOpen(state, kv.Key, false);
                CloneDocument(doc, state);
            }

            var manifest = new JObject
            {
                ["state"] = "complete",
                ["completed_at_utc"] = DateTime.UtcNow.ToString("O"),
                ["source_root"] = sourceRoot,
                ["destination_root"] = destinationRoot,
                ["top_document"] = mappedTop,
                ["file_count"] = map.Count,
                ["rebound_references"] = state.Rebound,
                ["external_references"] = state.ExternalReferences,
                ["warnings"] = state.Warnings,
                ["files"] = planned,
            };
            string manifestPath = Path.Combine(destinationRoot, "clone-manifest.json");
            File.WriteAllText(manifestPath, manifest.ToString());
            File.Delete(incompletePath);
            manifest["manifest"] = manifestPath;
            return Ok(ctx, manifest);
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(incompletePath, new JObject
                {
                    ["state"] = "failed",
                    ["failed_at_utc"] = DateTime.UtcNow.ToString("O"),
                    ["error"] = ex.Message,
                    ["completed_sources"] = new JArray(state.Completed.OrderBy(x => x)),
                    ["rebound_references"] = state.Rebound,
                    ["external_references"] = state.ExternalReferences,
                }.ToString());
            }
            catch { }
            return Fail(ctx, InventorErrorCodes.API_ERROR,
                "clone/recode failed; source files were not saved and INCOMPLETE.json was retained: " + ex.Message);
        }
        finally
        {
            // Close only documents opened by this command. Never close a document that the engineer already
            // had open before the job started.
            for (int i = state.OpenedByHandler.Count - 1; i >= 0; i--)
            {
                try { state.OpenedByHandler[i].Close(true); } catch { }
            }
        }
    }

    private static void CloneDocument(global::Inventor.Document doc, CloneState state)
    {
        string source = FullPath(doc.FullFileName);
        if (state.Completed.Contains(source)) return;
        if (!state.FileMap.TryGetValue(source, out string? target))
        {
            state.ExternalReferences.Add(source);
            return;
        }
        if (!state.Visiting.Add(source))
            throw new InvalidOperationException("circular document reference detected at " + source);

        var descriptors = new List<FileDescriptor>();
        foreach (FileDescriptor descriptor in doc.File.ReferencedFileDescriptors) descriptors.Add(descriptor);
        var changedReferences = new List<(FileDescriptor Descriptor, string Original)>();
        var properties = new List<PropertySnapshot>();

        Exception? restorationError = null;
        try
        {
            foreach (FileDescriptor descriptor in descriptors)
            {
                string original = FullPath(descriptor.FullFileName);
                if (!state.FileMap.TryGetValue(original, out string? referencedTarget))
                {
                    state.ExternalReferences.Add(new JObject
                    {
                        ["document"] = source,
                        ["reference"] = original,
                    });
                    continue;
                }

                if (NativeExtensions.Contains(Path.GetExtension(original)))
                {
                    global::Inventor.Document referenced = AvailableDocument(descriptor)
                        ?? FindOrOpen(state, original, false);
                    CloneDocument(referenced, state);
                }
                if (!File.Exists(referencedTarget))
                    throw new FileNotFoundException("copied reference is missing", referencedTarget);

                descriptor.ReplaceReference(referencedTarget);
                changedReferences.Add((descriptor, original));
                state.Rebound.Add(new JObject
                {
                    ["document"] = source,
                    ["from"] = original,
                    ["to"] = referencedTarget,
                });
            }

            if (state.UpdateIProperties)
                ApplyRecodedProperties(doc, source, target, state.SourceCode, state.TargetCode, properties, state.Warnings);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            doc.SaveAs(target, true); // SaveCopyAs: preserves InternalName required by ReplaceReference.
            state.Completed.Add(source);
        }
        finally
        {
            for (int i = properties.Count - 1; i >= 0; i--)
            {
                try { properties[i].Property.Value = properties[i].Value; }
                catch (Exception ex) { restorationError ??= ex; }
            }
            for (int i = changedReferences.Count - 1; i >= 0; i--)
            {
                try { changedReferences[i].Descriptor.ReplaceReference(changedReferences[i].Original); }
                catch (Exception ex) { restorationError ??= ex; }
            }
            if (restorationError is null)
            {
                // Source changes above existed only to produce the copy. Clearing Dirty is safe only after
                // every property and reference has been restored successfully.
                try { doc.Dirty = false; } catch { }
            }
            state.Visiting.Remove(source);
        }
        if (restorationError is not null)
            throw new InvalidOperationException(
                "failed to restore the in-memory source document after SaveCopyAs: " + source,
                restorationError);
    }

    private static void ApplyRecodedProperties(
        global::Inventor.Document doc,
        string sourcePath,
        string targetPath,
        string sourceCode,
        string targetCode,
        List<PropertySnapshot> snapshots,
        JArray warnings)
    {
        var propertyRefs = new[]
        {
            (Set: "Design Tracking Properties", Name: "Part Number"),
            (Set: "Design Tracking Properties", Name: "Stock Number"),
            (Set: "Design Tracking Properties", Name: "Description"),
            (Set: "Summary Information", Name: "Title"),
        };
        foreach (var item in propertyRefs)
        {
            try
            {
                PropertySet set = doc.PropertySets[item.Set];
                Inventor.Property property = set[item.Name];
                object oldValue = property.Value;
                string text = Convert.ToString(oldValue) ?? "";
                string recoded = ReplaceOrdinalIgnoreCase(text, sourceCode, targetCode);
                if (item.Name == "Part Number" && string.IsNullOrWhiteSpace(recoded))
                    recoded = Path.GetFileNameWithoutExtension(targetPath);
                if (string.Equals(text, recoded, StringComparison.Ordinal)) continue;
                snapshots.Add(new PropertySnapshot { Property = property, Value = oldValue });
                property.Value = recoded;
            }
            catch (Exception ex)
            {
                warnings.Add(new JObject
                {
                    ["document"] = sourcePath,
                    ["property"] = item.Name,
                    ["warning"] = ex.Message,
                });
            }
        }
    }

    private static global::Inventor.Document? AvailableDocument(FileDescriptor descriptor)
    {
        try
        {
            if (descriptor.ReferencedFile.AvailableDocuments.Count > 0)
                return descriptor.ReferencedFile.AvailableDocuments[1];
        }
        catch { }
        return null;
    }

    private static global::Inventor.Document FindOrOpen(CloneState state, string path, bool visible)
    {
        foreach (global::Inventor.Document open in state.App.Documents)
        {
            try
            {
                if (string.Equals(FullPath(open.FullFileName), path, StringComparison.OrdinalIgnoreCase))
                    return open;
            }
            catch { }
        }
        global::Inventor.Document opened = state.App.Documents.Open(path, visible);
        state.OpenedByHandler.Add(opened);
        return opened;
    }

    private static string ReplaceOrdinalIgnoreCase(string value, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(oldValue)) return value;
        int start = 0;
        var result = new System.Text.StringBuilder();
        while (true)
        {
            int index = value.IndexOf(oldValue, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) { result.Append(value, start, value.Length - start); break; }
            result.Append(value, start, index - start);
            result.Append(newValue);
            start = index + oldValue.Length;
        }
        return result.ToString();
    }

    private static bool IsUnder(string path, string root)
    {
        string fullPath = FullPath(path);
        string fullRoot = FullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string FullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path); } catch { return ""; }
    }

    private static string RelativeUnder(string path, string root)
    {
        string fullPath = FullPath(path);
        string fullRoot = FullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!IsUnder(fullPath, fullRoot)) throw new InvalidOperationException("path is outside root: " + fullPath);
        if (fullPath.Length == fullRoot.Length) return Path.GetFileName(fullPath);
        return fullPath.Substring(fullRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
#endif
