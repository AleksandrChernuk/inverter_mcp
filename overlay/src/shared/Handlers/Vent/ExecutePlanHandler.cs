#if INVENTOR2021 || INVENTOR2022 || INVENTOR2023 || INVENTOR2024 || INVENTOR2025 || INVENTOR2026 || INVENTOR2027
using System;
using System.Collections.Generic;
using Bimwright.Ipt.Shared.Contracts;
using Bimwright.Ipt.Shared.Handlers.Assembly;
using Bimwright.Ipt.Shared.Handlers.Document;
using Bimwright.Ipt.Shared.Infrastructure;
using Newtonsoft.Json.Linq;
using Inventor;

namespace Bimwright.Ipt.Shared.Handlers.Vent;

/// <summary>
/// <c>vent_execute_plan</c> — bounded, transaction-backed executor for an autonomous constructor.
/// A planner submits explicit mutations and acceptance checks. Every mutation is applied inside one
/// Inventor transaction, the document is rebuilt with <c>Update2(false)</c>, and failed acceptance
/// checks abort the transaction. Because an Inventor transaction is scoped to one Document while an
/// occurrence parameter belongs to a referenced Document, the executor also snapshots model/user
/// parameter expressions and part materials across the referenced document tree. On failure it aborts
/// the primary transaction, restores that snapshot, rebuilds the tree, and reports any restore error.
/// A successful plan is deliberately NOT saved to disk; saving is a separate approval-gated operation.
/// </summary>
public sealed class ExecutePlanHandler : HandlerBase, IInventorCommand
{
    public string Name => "vent_execute_plan";
    public bool IsReadOnly => false;

    private const int MaxMutations = 256;
    private const int MaxChecks = 64;
    private const double KgToG = 1000.0;

    public InventorCommandResult Execute(InventorCommandContext ctx, JObject p)
    {
        var app = (Application)ctx.Application!;
        global::Inventor.Document? doc;
        try { doc = app.ActiveDocument; } catch { doc = null; }
        if (doc is null)
            return Fail(ctx, InventorErrorCodes.NO_DOCUMENT, "no active Inventor document");

        var mutations = p["mutations"] as JArray ?? new JArray();
        var checks = p["checks"] as JArray ?? new JArray();
        bool dryRun = p.Value<bool?>("dry_run") ?? false;

        if (checks.Count == 0)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, "at least one acceptance check is required");
        if (mutations.Count > MaxMutations)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, $"at most {MaxMutations} mutations are allowed");
        if (checks.Count > MaxChecks)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, $"at most {MaxChecks} checks are allowed");

        string? invalid = ValidatePlan(mutations, checks);
        if (invalid is not null)
            return Fail(ctx, InventorErrorCodes.INVALID_ARGUMENT, invalid);

        if (dryRun)
        {
            return Ok(ctx, new JObject
            {
                ["pass"] = true,
                ["dry_run"] = true,
                ["committed"] = false,
                ["mutation_count"] = mutations.Count,
                ["check_count"] = checks.Count,
                ["document"] = doc.DisplayName,
                ["note"] = "Plan validated only; the Inventor model was not changed.",
            });
        }

        RollbackSnapshot? rollback = null;
        if (mutations.Count > 0)
        {
            try { rollback = RollbackSnapshot.Capture(doc); }
            catch (Exception ex)
            {
                return Fail(ctx, InventorErrorCodes.API_ERROR,
                    "cannot create pre-mutation rollback snapshot: " + ex.Message);
            }
        }

        global::Inventor.Transaction? transaction = null;
        var mutationEvidence = new JArray();
        var checkEvidence = new JArray();

        try
        {
            transaction = app.TransactionManager.StartTransaction((global::Inventor._Document)doc,
                mutations.Count == 0 ? "KVZ independent validation" : "KVZ autonomous engineering plan");

            for (int i = 0; i < mutations.Count; i++)
            {
                var step = (JObject)mutations[i]!;
                var result = RunMutation(ctx, step);
                mutationEvidence.Add(Evidence(i, step.Value<string>("kind")!, result));
                if (!result.Ok)
                    return AbortPlan(ctx, transaction, rollback, doc, "mutation_failed", mutationEvidence, checkEvidence);
            }

            bool updateOk;
            try { updateOk = doc.Update2(false); }
            catch (Exception ex)
            {
                checkEvidence.Add(new JObject
                {
                    ["kind"] = "document_update",
                    ["pass"] = false,
                    ["message"] = "final rebuild failed: " + ex.Message,
                });
                return AbortPlan(ctx, transaction, rollback, doc, "rebuild_exception", mutationEvidence, checkEvidence);
            }

            if (!updateOk)
            {
                checkEvidence.Add(new JObject
                {
                    ["kind"] = "document_update",
                    ["pass"] = false,
                    ["message"] = "Document.Update2(false) reported one or more model errors.",
                });
                return AbortPlan(ctx, transaction, rollback, doc, "rebuild_failed", mutationEvidence, checkEvidence);
            }

            for (int i = 0; i < checks.Count; i++)
            {
                var check = (JObject)checks[i]!;
                var evidence = RunCheck(ctx, doc, check);
                evidence["index"] = i;
                checkEvidence.Add(evidence);
                if (!(evidence.Value<bool?>("pass") ?? false))
                    return AbortPlan(ctx, transaction, rollback, doc, "acceptance_failed", mutationEvidence, checkEvidence);
            }

            transaction.End();
            transaction = null;
            return Ok(ctx, new JObject
            {
                ["pass"] = true,
                ["dry_run"] = false,
                ["committed"] = true,
                ["saved"] = false,
                ["document"] = doc.DisplayName,
                ["rollback_snapshot"] = rollback?.Summary(),
                ["mutations"] = mutationEvidence,
                ["checks"] = checkEvidence,
                ["note"] = mutations.Count == 0
                    ? "Independent validation passed; the document was not saved."
                    : "All checks passed. Changes remain in memory and are not saved to disk.",
            });
        }
        catch (Exception ex)
        {
            checkEvidence.Add(new JObject
            {
                ["kind"] = "executor_exception",
                ["pass"] = false,
                ["message"] = ex.Message,
            });
            return AbortPlan(ctx, transaction, rollback, doc, "executor_exception", mutationEvidence, checkEvidence);
        }
    }

    private static InventorCommandResult AbortPlan(
        InventorCommandContext ctx,
        global::Inventor.Transaction? transaction,
        RollbackSnapshot? rollback,
        global::Inventor.Document doc,
        string reason,
        JArray mutations,
        JArray checks)
    {
        bool transactionAborted = AbortQuietly(transaction, out string? transactionError);
        JObject restore = rollback?.Restore() ?? new JObject
        {
            ["pass"] = true,
            ["document_count"] = 0,
            ["parameter_count"] = 0,
            ["errors"] = new JArray(),
        };
        bool restored = restore.Value<bool?>("pass") ?? false;
        return Ok(ctx, new JObject
        {
            ["pass"] = false,
            ["dry_run"] = false,
            ["committed"] = false,
            ["saved"] = false,
            ["rolled_back"] = transactionAborted && restored,
            ["transaction_aborted"] = transactionAborted,
            ["transaction_error"] = transactionError,
            ["snapshot_restore"] = restore,
            ["reason"] = reason,
            ["document"] = doc.DisplayName,
            ["mutations"] = mutations,
            ["checks"] = checks,
        });
    }

    private static bool AbortQuietly(global::Inventor.Transaction? transaction, out string? error)
    {
        error = null;
        if (transaction is null) return true;
        try
        {
            transaction.Abort();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Inventor transactions have a Document owner. Mutating an occurrence parameter therefore crosses
    /// the active assembly transaction boundary. This bounded state snapshot is the second rollback layer:
    /// only driving expressions and active part materials are captured; geometry is regenerated from them.
    /// Reference parameters are intentionally excluded because Inventor computes them and does not allow
    /// assigning their expressions.
    /// </summary>
    private sealed class RollbackSnapshot
    {
        private const int MaxDocuments = 512;
        private const int MaxParameters = 100000;

        private sealed class ParameterState
        {
            public Parameter Parameter { get; }
            public string Expression { get; }
            public string Label { get; }

            public ParameterState(Parameter parameter, string expression, string label)
            {
                Parameter = parameter;
                Expression = expression;
                Label = label;
            }
        }

        private sealed class DocumentState
        {
            public global::Inventor.Document Document { get; }
            public bool WasDirty { get; }
            public List<ParameterState> Parameters { get; } = new();
            public Asset? ActiveMaterial { get; set; }
            public bool HasActiveMaterial { get; set; }

            public DocumentState(global::Inventor.Document document, bool wasDirty)
            {
                Document = document;
                WasDirty = wasDirty;
            }
        }

        private readonly List<DocumentState> _documents;
        private readonly int _parameterCount;

        private RollbackSnapshot(List<DocumentState> documents, int parameterCount)
        {
            _documents = documents;
            _parameterCount = parameterCount;
        }

        public static RollbackSnapshot Capture(global::Inventor.Document root)
        {
            var documents = new List<global::Inventor.Document> { root };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DocumentKey(root) };

            foreach (global::Inventor.Document referenced in root.AllReferencedDocuments)
            {
                string key = DocumentKey(referenced);
                if (seen.Add(key)) documents.Add(referenced);
                if (documents.Count > MaxDocuments)
                    throw new InvalidOperationException($"rollback snapshot exceeds {MaxDocuments} documents");
            }

            var states = new List<DocumentState>(documents.Count);
            int parameterCount = 0;
            foreach (var document in documents)
            {
                bool dirty;
                try { dirty = document.Dirty; }
                catch { dirty = true; }

                var state = new DocumentState(document, dirty);
                global::Inventor.Parameters? parameters = document switch
                {
                    PartDocument part => part.ComponentDefinition.Parameters,
                    AssemblyDocument assembly => assembly.ComponentDefinition.Parameters,
                    _ => null,
                };
                if (parameters is not null)
                {
                    CaptureParameters(parameters.UserParameters, document, state);
                    CaptureParameters(parameters.ModelParameters, document, state);
                }

                if (document is PartDocument partDocument)
                {
                    try
                    {
                        state.ActiveMaterial = partDocument.ActiveMaterial;
                        state.HasActiveMaterial = state.ActiveMaterial is not null;
                    }
                    catch { /* material may be unavailable for a special document subtype */ }
                }

                parameterCount += state.Parameters.Count;
                if (parameterCount > MaxParameters)
                    throw new InvalidOperationException($"rollback snapshot exceeds {MaxParameters} driving parameters");
                states.Add(state);
            }
            return new RollbackSnapshot(states, parameterCount);
        }

        private static void CaptureParameters(
            System.Collections.IEnumerable collection,
            global::Inventor.Document document,
            DocumentState state)
        {
            foreach (Parameter parameter in collection)
            {
                try
                {
                    state.Parameters.Add(new ParameterState(
                        parameter,
                        parameter.Expression,
                        document.DisplayName + ":" + parameter.Name));
                }
                catch
                {
                    // A read-protected/non-expression parameter cannot be mutated by the bounded handlers.
                }
            }
        }

        public JObject Summary() => new()
        {
            ["document_count"] = _documents.Count,
            ["parameter_count"] = _parameterCount,
            ["captures_material"] = true,
        };

        public JObject Restore()
        {
            var errors = new JArray();
            var changed = new HashSet<DocumentState>();
            for (int d = _documents.Count - 1; d >= 0; d--)
            {
                var state = _documents[d];
                for (int i = state.Parameters.Count - 1; i >= 0; i--)
                {
                    var parameter = state.Parameters[i];
                    try
                    {
                        if (!string.Equals(parameter.Parameter.Expression, parameter.Expression, StringComparison.Ordinal))
                        {
                            parameter.Parameter.Expression = parameter.Expression;
                            changed.Add(state);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add(parameter.Label + ": " + ex.Message);
                    }
                }

                if (state.HasActiveMaterial && state.Document is PartDocument part && state.ActiveMaterial is not null)
                {
                    try
                    {
                        string current = part.ActiveMaterial?.Name ?? "";
                        string expected = state.ActiveMaterial.Name;
                        if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
                        {
                            part.ActiveMaterial = state.ActiveMaterial;
                            changed.Add(state);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add(state.Document.DisplayName + ":material: " + ex.Message);
                    }
                }
            }

            // Rebuild only documents that were manually restored, then the root so nested assembly
            // geometry observes those restored values. Avoid touching unrelated external references.
            for (int d = _documents.Count - 1; d >= 0; d--)
            {
                if (!changed.Contains(_documents[d])) continue;
                try
                {
                    if (!_documents[d].Document.Update2(false))
                        errors.Add(_documents[d].Document.DisplayName + ": Update2(false) returned false");
                }
                catch (Exception ex)
                {
                    errors.Add(_documents[d].Document.DisplayName + ":rebuild: " + ex.Message);
                }
            }
            if (changed.Count > 0 && !changed.Contains(_documents[0]))
            {
                try
                {
                    if (!_documents[0].Document.Update2(false))
                        errors.Add(_documents[0].Document.DisplayName + ": root Update2(false) returned false");
                    changed.Add(_documents[0]);
                }
                catch (Exception ex)
                {
                    errors.Add(_documents[0].Document.DisplayName + ":root rebuild: " + ex.Message);
                }
            }

            if (errors.Count == 0)
            {
                foreach (var state in changed)
                {
                    if (state.WasDirty) continue;
                    try { state.Document.Dirty = false; }
                    catch (Exception ex) { errors.Add(state.Document.DisplayName + ":dirty flag: " + ex.Message); }
                }
            }

            return new JObject
            {
                ["pass"] = errors.Count == 0,
                ["document_count"] = _documents.Count,
                ["parameter_count"] = _parameterCount,
                ["restored_document_count"] = changed.Count,
                ["errors"] = errors,
            };
        }

        private static string DocumentKey(global::Inventor.Document document)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(document.FullDocumentName))
                    return document.FullDocumentName;
            }
            catch { }
            try { return document.DisplayName; }
            catch { return document.DisplayName; }
        }
    }

    private static InventorCommandResult RunMutation(InventorCommandContext ctx, JObject step)
    {
        return step.Value<string>("kind") switch
        {
            "drive_dimension" => new DriveDimensionHandler().Execute(ctx, step),
            "set_component_parameter" => new SetComponentParameterHandler().Execute(ctx, step),
            "set_constraint" => new SetConstraintHandler().Execute(ctx, step),
            "set_casing_discharge" => new SetDischargeHandler().Execute(ctx, step),
            "set_material" => new SetMaterialHandler().Execute(ctx, step),
            _ => throw new InvalidOperationException("unsupported mutation kind"),
        };
    }

    private static JObject RunCheck(InventorCommandContext ctx, global::Inventor.Document doc, JObject check)
    {
        string kind = check.Value<string>("kind")!;
        return kind switch
        {
            "model_health" => CheckModelHealth(doc),
            "part_cut_ready" => CheckPartReady(ctx),
            "constraints_healthy" => CheckConstraints(ctx, check),
            "no_interference" => CheckInterference(ctx, check),
            "min_distance" => CheckMinDistance(ctx, check),
            "physical_bounds" => CheckPhysicalBounds(doc, check),
            "mounting_pattern" => CheckMountingPattern(ctx, check),
            _ => new JObject { ["kind"] = kind, ["pass"] = false, ["message"] = "unsupported check kind" },
        };
    }

    private static JObject CheckMountingPattern(InventorCommandContext ctx, JObject check)
    {
        var args = (JObject)check.DeepClone();
        args.Remove("kind");
        var result = new CheckMountingPatternHandler().Execute(ctx, args);
        bool pass = result.Ok && (result.Data?["pass"]?.Value<bool>() ?? false);
        return ResultEvidence("mounting_pattern", pass, result);
    }

    private static JObject CheckModelHealth(global::Inventor.Document doc)
    {
        var unhealthy = new JArray();
        if (doc is PartDocument part)
        {
            try
            {
                foreach (PartFeature feature in part.ComponentDefinition.Features)
                {
                    bool suppressed = false;
                    try { suppressed = feature.Suppressed; } catch { }
                    if (suppressed) continue;

                    HealthStatusEnum health;
                    try { health = feature.HealthStatus; }
                    catch { continue; }
                    if (health != HealthStatusEnum.kUpToDateHealth)
                    {
                        unhealthy.Add(new JObject
                        {
                            ["name"] = feature.Name,
                            ["health"] = health.ToString(),
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                return new JObject { ["kind"] = "model_health", ["pass"] = false, ["message"] = ex.Message };
            }
        }

        return new JObject
        {
            ["kind"] = "model_health",
            ["pass"] = unhealthy.Count == 0,
            ["unhealthy_count"] = unhealthy.Count,
            ["unhealthy"] = unhealthy,
        };
    }

    private static JObject CheckPartReady(InventorCommandContext ctx)
    {
        var result = new CheckPartHandler().Execute(ctx, new JObject());
        bool pass = result.Ok && (result.Data?["pass"]?.Value<bool>() ?? false);
        return ResultEvidence("part_cut_ready", pass, result);
    }

    private static JObject CheckConstraints(InventorCommandContext ctx, JObject check)
    {
        var result = new ListConstraintsHandler().Execute(ctx, new JObject());
        if (!result.Ok) return ResultEvidence("constraints_healthy", false, result);

        int unhealthy = 0;
        foreach (var item in result.Data?["constraints"] as JArray ?? new JArray())
        {
            if (item.Value<bool?>("suppressed") == true) continue;
            if (!string.Equals(item.Value<string>("health"), "up_to_date", StringComparison.OrdinalIgnoreCase))
                unhealthy++;
        }
        int allowed = check.Value<int?>("allowed_unhealthy") ?? 0;
        var evidence = ResultEvidence("constraints_healthy", unhealthy <= allowed, result);
        evidence["unhealthy_count"] = unhealthy;
        evidence["allowed_unhealthy"] = allowed;
        return evidence;
    }

    private static JObject CheckInterference(InventorCommandContext ctx, JObject check)
    {
        var args = new JObject();
        if (check["occurrences"] is JArray occurrences)
            args["occurrences"] = occurrences.DeepClone();
        var result = new CheckInterferenceHandler().Execute(ctx, args);
        int count = result.Data?["count"]?.Value<int>() ?? int.MaxValue;
        int allowed = check.Value<int?>("max_pairs") ?? 0;
        var evidence = ResultEvidence("no_interference", result.Ok && count <= allowed, result);
        evidence["pair_count"] = count == int.MaxValue ? null : count;
        evidence["max_pairs"] = allowed;
        return evidence;
    }

    private static JObject CheckMinDistance(InventorCommandContext ctx, JObject check)
    {
        var a = check["a"] as JObject ?? new JObject();
        var b = check["b"] as JObject ?? new JObject();
        var args = new JObject
        {
            ["a_occurrence"] = a.Value<string>("occurrence"),
            ["a_ref"] = a.Value<string>("ref"),
            ["b_occurrence"] = b.Value<string>("occurrence"),
            ["b_ref"] = b.Value<string>("ref"),
        };
        var result = new MeasureMinDistanceHandler().Execute(ctx, args);
        double? distance = result.Data?["distance_mm"]?.Value<double>();
        double? minimum = check.Value<double?>("min_mm");
        double? maximum = check.Value<double?>("max_mm");
        bool pass = result.Ok && distance.HasValue
            && (!minimum.HasValue || distance.Value >= minimum.Value)
            && (!maximum.HasValue || distance.Value <= maximum.Value);
        var evidence = ResultEvidence("min_distance", pass, result);
        evidence["distance_mm"] = distance;
        evidence["min_mm"] = minimum;
        evidence["max_mm"] = maximum;
        return evidence;
    }

    private static JObject CheckPhysicalBounds(global::Inventor.Document doc, JObject check)
    {
        ComponentDefinition? definition = doc switch
        {
            PartDocument part => (global::Inventor.ComponentDefinition)part.ComponentDefinition,
            AssemblyDocument assembly => (global::Inventor.ComponentDefinition)assembly.ComponentDefinition,
            _ => null,
        };
        if (definition is null)
            return new JObject { ["kind"] = "physical_bounds", ["pass"] = false, ["message"] = "part or assembly required" };

        JObject bbox;
        double massG;
        try
        {
            bbox = VentSupport.BBoxMm(definition);
            massG = Math.Round(doc switch
            {
                PartDocument part => part.ComponentDefinition.MassProperties.Mass * KgToG,
                AssemblyDocument assembly => assembly.ComponentDefinition.MassProperties.Mass * KgToG,
                _ => throw new InvalidOperationException("part or assembly required"),
            }, 3);
        }
        catch (Exception ex)
        {
            return new JObject { ["kind"] = "physical_bounds", ["pass"] = false, ["message"] = ex.Message };
        }

        bool pass = Within(massG, check, "min_mass_g", "max_mass_g")
            && Within(bbox.Value<double>("x_mm"), check, "min_x_mm", "max_x_mm")
            && Within(bbox.Value<double>("y_mm"), check, "min_y_mm", "max_y_mm")
            && Within(bbox.Value<double>("z_mm"), check, "min_z_mm", "max_z_mm");

        return new JObject
        {
            ["kind"] = "physical_bounds",
            ["pass"] = pass,
            ["mass_g"] = massG,
            ["bounding_box_mm"] = bbox,
            ["expected"] = check.DeepClone(),
        };
    }

    private static bool Within(double actual, JObject limits, string minKey, string maxKey)
    {
        double? min = limits.Value<double?>(minKey);
        double? max = limits.Value<double?>(maxKey);
        return (!min.HasValue || actual >= min.Value) && (!max.HasValue || actual <= max.Value);
    }

    private static JObject Evidence(int index, string kind, InventorCommandResult result)
    {
        var evidence = ResultEvidence(kind, result.Ok, result);
        evidence["index"] = index;
        return evidence;
    }

    private static JObject ResultEvidence(string kind, bool pass, InventorCommandResult result)
    {
        return new JObject
        {
            ["kind"] = kind,
            ["pass"] = pass,
            ["data"] = result.Data?.DeepClone(),
            ["error"] = result.Error is null ? null : JObject.FromObject(result.Error),
        };
    }

    private static string? ValidatePlan(JArray mutations, JArray checks)
    {
        for (int i = 0; i < mutations.Count; i++)
        {
            if (mutations[i] is not JObject step)
                return $"mutations[{i}] must be an object";
            string? kind = step.Value<string>("kind");
            if (kind is not ("drive_dimension" or "set_component_parameter" or "set_constraint" or "set_casing_discharge" or "set_material"))
                return $"mutations[{i}].kind is unsupported";
        }

        for (int i = 0; i < checks.Count; i++)
        {
            if (checks[i] is not JObject check)
                return $"checks[{i}] must be an object";
            string? kind = check.Value<string>("kind");
            if (kind is not ("model_health" or "part_cut_ready" or "constraints_healthy" or "no_interference" or "min_distance" or "physical_bounds" or "mounting_pattern"))
                return $"checks[{i}].kind is unsupported";
        }
        return null;
    }
}
#endif
