using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System.Numerics;

namespace MateriaRetreive.Windows;

public sealed class BulkRetrieveWindow : Window, IDisposable
{
    private static readonly Vector4 PanelBlue = new(0.04f, 0.20f, 0.47f, 0.88f);
    private static readonly Vector4 PanelBlueDeep = new(0.03f, 0.13f, 0.31f, 0.94f);
    private static readonly Vector4 PanelBlueLight = new(0.20f, 0.45f, 0.84f, 0.72f);
    private static readonly Vector4 BorderBlue = new(0.36f, 0.66f, 1.00f, 0.45f);
    private static readonly Vector4 HeaderText = new(0.90f, 0.96f, 1.00f, 1.00f);
    private static readonly Vector4 MutedText = new(0.64f, 0.77f, 0.94f, 0.95f);
    private static readonly Vector4 ItemGreen = new(0.40f, 0.88f, 0.56f, 1.00f);
    private static readonly Vector4 ItemBlue = new(0.38f, 0.72f, 1.00f, 1.00f);

    private readonly Func<MateriaFilter, IReadOnlyList<MateriaCandidate>> scanCandidates;
    private readonly Action<IReadOnlyList<MateriaCandidate>> startRetrieval;
    private readonly Action cancelRetrieval;
    private readonly Queue<QueuedMateriaItem> queue;
    private readonly List<QueuedMateriaItem> finished;
    private IReadOnlyList<MateriaCandidate> candidates = [];
    private MateriaFilter filter = MateriaFilter.NonGearset;

    public BulkRetrieveWindow(
        Func<MateriaFilter, IReadOnlyList<MateriaCandidate>> scanCandidates,
        Action<IReadOnlyList<MateriaCandidate>> startRetrieval,
        Action cancelRetrieval,
        Queue<QueuedMateriaItem> queue,
        List<QueuedMateriaItem> finished)
        : base("Retreive Materia###MateriaRetreiveBulk", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar)
    {
        this.scanCandidates = scanCandidates;
        this.startRetrieval = startRetrieval;
        this.cancelRetrieval = cancelRetrieval;
        this.queue = queue;
        this.finished = finished;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(420, 260),
            MaximumSize = new(760, 720),
        };
        this.Size = new(520, 420);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public IReadOnlyList<MateriaCandidate> Refresh()
    {
        this.candidates = this.scanCandidates(this.filter);
        return this.candidates;
    }

    public void SetCandidates(IReadOnlyList<MateriaCandidate> candidates)
        => this.candidates = candidates;

    public override void Draw()
    {
        this.PushMateriaStyle();
        try
        {
            this.DrawHeader();
            this.DrawFilterBar();
            this.DrawItemsTable();
            this.DrawActionBar();
            this.DrawProgress();
        }
        finally
        {
            ImGui.PopStyleColor(15);
            ImGui.PopStyleVar(6);
        }
    }

    private void DrawHeader()
    {
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var max = min + new Vector2(width, 46);
        drawList.AddRectFilledMultiColor(
            min,
            max,
            ImGui.GetColorU32(PanelBlueLight),
            ImGui.GetColorU32(PanelBlue),
            ImGui.GetColorU32(PanelBlueDeep),
            ImGui.GetColorU32(PanelBlue));
        drawList.AddRect(min, max, ImGui.GetColorU32(BorderBlue));

        ImGui.SetCursorScreenPos(min + new Vector2(8, 5));
        ImGui.TextColored(HeaderText, "RETREIVE MATERIA");

        ImGui.SetCursorScreenPos(min + new Vector2(10, 26));
        ImGui.TextColored(MutedText, $"{this.candidates.Count} eligible item(s)");

        ImGui.SetCursorScreenPos(new Vector2(max.X - 92, min.Y + 12));
        if (ImGui.Button("Refresh", new(84, 24)))
            this.Refresh();

        ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y + 6));
    }

    private void DrawFilterBar()
    {
        ImGui.TextColored(HeaderText, "GEAR");

        var availableWidth = ImGui.GetContentRegionAvail().X;
        if (ImGui.BeginTable("##MateriaRetreiveFilters", 3, ImGuiTableFlags.SizingStretchSame, new(availableWidth, 30)))
        {
            ImGui.TableNextColumn();
            this.DrawFilterButton("Non-gearset", MateriaFilter.NonGearset);

            ImGui.TableNextColumn();
            this.DrawFilterButton("Gearset", MateriaFilter.Gearset);

            ImGui.TableNextColumn();
            this.DrawFilterButton("Ready", MateriaFilter.Ready);

            ImGui.EndTable();
        }
    }

    private void DrawFilterButton(string label, MateriaFilter filter)
    {
        var selected = this.filter == filter;
        if (selected)
            ImGui.PushStyleColor(ImGuiCol.Button, PanelBlueLight);

        var clicked = ImGui.Button(selected ? $"* {label}" : label, new(ImGui.GetContentRegionAvail().X, 24));

        if (selected)
            ImGui.PopStyleColor();

        if (!clicked || selected)
            return;

        this.filter = filter;
        this.Refresh();
    }

    private void DrawItemsTable()
    {
        var tableHeight = MathF.Max(190, ImGui.GetContentRegionAvail().Y - 82);
        var flags = ImGuiTableFlags.BordersInnerV
            | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.ScrollY
            | ImGuiTableFlags.SizingStretchProp
            | ImGuiTableFlags.Resizable;

        if (!ImGui.BeginTable("##MateriaRetreiveItems", 6, flags, new(0, tableHeight)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3.2f);
        ImGui.TableSetupColumn("Materia", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Spiritbond", ImGuiTableColumnFlags.WidthFixed, 86);
        ImGui.TableSetupColumn("Gearset", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Container", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 44);
        ImGui.TableHeadersRow();

        foreach (var candidate in this.candidates)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(candidate.IsInGearset ? ItemBlue : ItemGreen, candidate.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(candidate.MateriaCount.ToString());
            ImGui.TableNextColumn();
            ImGui.TextColored(candidate.CanExtractMateria ? ItemGreen : MutedText, $"{candidate.SpiritbondPercent}%");
            ImGui.TableNextColumn();
            ImGui.TextColored(candidate.IsInGearset ? ItemBlue : MutedText, candidate.IsInGearset ? "Yes" : "No");
            ImGui.TableNextColumn();
            ImGui.TextColored(MutedText, candidate.Container.ToString());
            ImGui.TableNextColumn();
            ImGui.TextColored(MutedText, (candidate.Slot + 1).ToString());
        }

        ImGui.EndTable();
    }

    private void DrawActionBar()
    {
        var busy = this.queue.Count > 0;
        var width = ImGui.GetContentRegionAvail().X;

        if (busy)
            ImGui.BeginDisabled();

        if (ImGui.Button("Retreive All", new(busy ? width - 128 : width, 28)))
            this.startRetrieval(this.candidates);

        if (busy)
            ImGui.EndDisabled();

        if (!busy)
            return;

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new(120, 28)))
            this.cancelRetrieval();
    }

    private void DrawProgress()
    {
        if (this.queue.Count > 0 || this.finished.Count > 0)
        {
            ImGui.TextColored(MutedText, $"Queued: {this.queue.Count}    Finished: {this.finished.Count}");
            if (this.finished.Count > 0)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear finished"))
                    this.finished.Clear();
            }

            foreach (var item in this.queue.Take(5))
                ImGui.TextColored(ItemGreen, $"{item.Name}: {item.StartingMateriaCount - item.CurrentMateriaCount} / {item.StartingMateriaCount}");
        }
    }

    private void PushMateriaStyle()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(3, 3));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(7, 3));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 4));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 0);

        ImGui.PushStyleColor(ImGuiCol.WindowBg, PanelBlueDeep);
        ImGui.PushStyleColor(ImGuiCol.Border, BorderBlue);
        ImGui.PushStyleColor(ImGuiCol.Text, HeaderText);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, MutedText);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.38f, 0.56f, 0.80f, 0.80f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.48f, 0.70f, 1.00f, 0.90f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.24f, 0.44f, 0.82f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.Header, PanelBlueLight);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.28f, 0.52f, 0.94f, 0.86f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.18f, 0.38f, 0.78f, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, new Vector4(0.05f, 0.18f, 0.42f, 0.96f));
        ImGui.PushStyleColor(ImGuiCol.TableRowBg, new Vector4(0.04f, 0.15f, 0.34f, 0.45f));
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, new Vector4(0.06f, 0.22f, 0.50f, 0.45f));
        ImGui.PushStyleColor(ImGuiCol.TableBorderLight, BorderBlue);
        ImGui.PushStyleColor(ImGuiCol.TableBorderStrong, BorderBlue);
    }

    public void Dispose()
    {
    }
}

public enum MateriaFilter
{
    NonGearset,
    Gearset,
    Ready,
}
