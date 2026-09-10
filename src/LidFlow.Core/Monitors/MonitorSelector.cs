using System;
using System.Collections.Generic;

namespace LidFlow.Core.Monitors;

/// <summary>
/// Decides which displays the transition is drawn on.
/// <para>
/// The default is deliberately conservative. A lid-close animation on an external monitor
/// would be nonsense — that panel is not moving — so unless the user opts in, only the
/// built-in panel is animated, and if the built-in panel cannot be identified the code
/// falls back rather than guessing wide.
/// </para>
/// </summary>
public static class MonitorSelector
{
    public static MonitorSelectionResult Select(
        IReadOnlyList<DisplayInfo> displays,
        MonitorSelectionMode mode,
        string? manualDisplayId = null,
        bool allowExternalDisplays = false)
    {
        if (displays is null || displays.Count == 0)
        {
            return new MonitorSelectionResult(Array.Empty<DisplayInfo>(), MonitorSelectionReason.NoTarget);
        }

        switch (mode)
        {
            case MonitorSelectionMode.AllDisplays:
                if (!allowExternalDisplays)
                {
                    // Asking for every display while forbidding external ones is
                    // contradictory; honour the restriction, which is the safe reading.
                    MonitorSelectionResult restricted = SelectInternal(displays);
                    return new MonitorSelectionResult(
                        restricted.Targets,
                        restricted.HasTarget
                            ? MonitorSelectionReason.ExternalDisplaysNotAllowed
                            : MonitorSelectionReason.NoTarget);
                }

                return new MonitorSelectionResult(displays, MonitorSelectionReason.Exact);

            case MonitorSelectionMode.PrimaryDisplay:
            {
                DisplayInfo primary = FindPrimary(displays);
                if (!allowExternalDisplays && !primary.IsInternalPanel)
                {
                    MonitorSelectionResult internalOnly = SelectInternal(displays);
                    if (internalOnly.HasTarget)
                    {
                        return new MonitorSelectionResult(
                            internalOnly.Targets,
                            MonitorSelectionReason.ExternalDisplaysNotAllowed);
                    }
                }

                return new MonitorSelectionResult(new[] { primary }, MonitorSelectionReason.Exact);
            }

            case MonitorSelectionMode.Manual:
            {
                DisplayInfo? match = FindById(displays, manualDisplayId);
                if (match is null)
                {
                    MonitorSelectionResult fallback = SelectInternal(displays);
                    return new MonitorSelectionResult(
                        fallback.Targets,
                        fallback.HasTarget
                            ? MonitorSelectionReason.ManualTargetMissing
                            : MonitorSelectionReason.NoTarget);
                }

                if (!allowExternalDisplays && !match.IsInternalPanel)
                {
                    MonitorSelectionResult internalOnly = SelectInternal(displays);
                    if (internalOnly.HasTarget)
                    {
                        return new MonitorSelectionResult(
                            internalOnly.Targets,
                            MonitorSelectionReason.ExternalDisplaysNotAllowed);
                    }
                }

                return new MonitorSelectionResult(new[] { match }, MonitorSelectionReason.Exact);
            }

            case MonitorSelectionMode.InternalPanel:
            default:
                return SelectInternal(displays);
        }
    }

    private static MonitorSelectionResult SelectInternal(IReadOnlyList<DisplayInfo> displays)
    {
        // Prefer the most confidently-identified internal panel.
        DisplayInfo? best = null;

        for (int i = 0; i < displays.Count; i++)
        {
            DisplayInfo candidate = displays[i];
            if (candidate.Bounds.IsEmpty || candidate.InternalConfidence < InternalPanelConfidence.Likely)
            {
                continue;
            }

            if (best is null || candidate.InternalConfidence > best.InternalConfidence)
            {
                best = candidate;
            }
        }

        if (best is not null)
        {
            return new MonitorSelectionResult(new[] { best }, MonitorSelectionReason.Exact);
        }

        // No connector said "internal". On a single-display machine the only display is
        // the only thing a lid could possibly belong to, so use it. With several displays
        // and no way to tell them apart, fall back to primary and record that this was a
        // guess so the UI and the log can say so.
        if (displays.Count == 1 && !displays[0].Bounds.IsEmpty)
        {
            return new MonitorSelectionResult(new[] { displays[0] }, MonitorSelectionReason.InternalPanelNotIdentified);
        }

        DisplayInfo primary = FindPrimary(displays);
        return primary.Bounds.IsEmpty
            ? new MonitorSelectionResult(Array.Empty<DisplayInfo>(), MonitorSelectionReason.NoTarget)
            : new MonitorSelectionResult(new[] { primary }, MonitorSelectionReason.InternalPanelNotIdentified);
    }

    private static DisplayInfo FindPrimary(IReadOnlyList<DisplayInfo> displays)
    {
        for (int i = 0; i < displays.Count; i++)
        {
            if (displays[i].IsPrimary && !displays[i].Bounds.IsEmpty)
            {
                return displays[i];
            }
        }

        for (int i = 0; i < displays.Count; i++)
        {
            if (!displays[i].Bounds.IsEmpty)
            {
                return displays[i];
            }
        }

        return displays[0];
    }

    private static DisplayInfo? FindById(IReadOnlyList<DisplayInfo> displays, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        // Device paths survive reboots and re-enumeration better than \\.\DISPLAYn, so
        // match on them first.
        for (int i = 0; i < displays.Count; i++)
        {
            if (!string.IsNullOrEmpty(displays[i].DevicePath) &&
                string.Equals(displays[i].DevicePath, id, StringComparison.OrdinalIgnoreCase))
            {
                return displays[i];
            }
        }

        for (int i = 0; i < displays.Count; i++)
        {
            if (string.Equals(displays[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return displays[i];
            }
        }

        return null;
    }
}
