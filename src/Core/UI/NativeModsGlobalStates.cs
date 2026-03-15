using System.Linq;
using Terraria.UI;

namespace TerrariaModder.Core.UI
{
    internal sealed class LoadOrderState : NativeModsStateBase
    {
        private readonly System.Collections.Generic.List<string> _order;

        public LoadOrderState(NativeModsService service, UIState previousState, bool inGame)
            : base(service, previousState, inGame)
        {
            _order = service.GetEditableLoadOrder();
        }

        protected override string GetTitle() => "Load Order";

        protected override void RebuildList()
        {
            List.Clear();
            List.Add(CreateInfoRow("Left click: move down. Right click: move up."));
            List.Add(CreateButton("Save Load Order", () =>
            {
                Service.SaveLoadOrder(_order);
                SetStatus("Load order saved.");
            }));

            for (int i = 0; i < _order.Count; i++)
            {
                int index = i;
                string modId = _order[i];
                var mod = PluginLoader.GetMod(modId);
                string display = $"{i + 1}. {(mod?.Manifest?.Name ?? modId)}";
                var row = CreateButton(display, () => Move(index, index + 1), 38f);
                row.OnRightClick += (_, __) => Move(index, index - 1);
                List.Add(row);
            }
        }

        private void Move(int fromIndex, int toIndex)
        {
            if (toIndex < 0 || toIndex >= _order.Count) return;

            if (Service.MoveLoadOrderEntry(_order, fromIndex, toIndex, out string error))
            {
                SetStatus("Load order updated.");
                RebuildList();
            }
            else
            {
                SetStatus(error);
            }
        }
    }

    internal sealed class ConflictsState : NativeModsStateBase
    {
        public ConflictsState(NativeModsService service, UIState previousState, bool inGame)
            : base(service, previousState, inGame)
        {
        }

        protected override string GetTitle() => "Conflicts";

        protected override void RebuildList()
        {
            List.Clear();
            var report = Service.GetConflictReport(forceRefresh: true);
            List.Add(CreateInfoRow($"Patch conflicts: {report.PatchConflicts.Count}"));
            List.Add(CreateInfoRow($"Keybind conflicts: {report.KeybindConflicts.Count}"));

            foreach (var conflict in report.PatchConflicts)
            {
                string owners = string.Join(", ", conflict.Patches.Select(p => $"{p.ModName} {p.PatchType}"));
                List.Add(CreateInfoRow($"{conflict.TargetType}.{conflict.TargetMethod}: {owners}", 44f));
            }

            foreach (var conflict in report.KeybindConflicts)
            {
                List.Add(CreateInfoRow($"{conflict.Keybind1.Label} and {conflict.Keybind2.Label} -> {conflict.ConflictingKey}", 40f));
            }

            if (report.PatchConflicts.Count == 0 && report.KeybindConflicts.Count == 0)
                List.Add(CreateInfoRow("No conflicts detected."));
        }
    }

    internal sealed class LogsState : NativeModsStateBase
    {
        public LogsState(NativeModsService service, UIState previousState, bool inGame)
            : base(service, previousState, inGame)
        {
        }

        protected override string GetTitle() => "Logs";

        protected override void RebuildList()
        {
            List.Clear();
            foreach (var entry in Service.GetRecentLogs(80))
            {
                string text = $"[{entry.Timestamp:HH:mm:ss}] [{entry.Level}] [{entry.ModId}] {entry.Message}";
                List.Add(CreateInfoRow(text, 42f));
            }

            if (List.Count == 0)
                List.Add(CreateInfoRow("No log messages yet."));
        }
    }
}
