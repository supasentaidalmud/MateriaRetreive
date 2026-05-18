using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MateriaRetreive.Windows;

public sealed class BulkRetrieveWindow : Window, IDisposable
{
    private readonly Func<bool, IReadOnlyList<MateriaCandidate>> scanCandidates;
    private readonly Action<IReadOnlyList<MateriaCandidate>> startRetrieval;
    private readonly Action cancelRetrieval;
    private readonly Queue<QueuedMateriaItem> queue;
    private readonly List<QueuedMateriaItem> finished;
    private IReadOnlyList<MateriaCandidate> candidates = [];
    private bool showGearsetItems;

    public BulkRetrieveWindow(
        Func<bool, IReadOnlyList<MateriaCandidate>> scanCandidates,
        Action<IReadOnlyList<MateriaCandidate>> startRetrieval,
        Action cancelRetrieval,
        Queue<QueuedMateriaItem> queue,
        List<QueuedMateriaItem> finished)
        : base("Retrieve Materia###MateriaRetreiveBulk", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.scanCandidates = scanCandidates;
        this.startRetrieval = startRetrieval;
        this.cancelRetrieval = cancelRetrieval;
        this.queue = queue;
        this.finished = finished;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(420, 260),
            MaximumSize = new(720, 720),
        };
    }

    public IReadOnlyList<MateriaCandidate> Refresh()
    {
        this.candidates = this.scanCandidates(this.showGearsetItems);
        return this.candidates;
    }

    public void SetCandidates(IReadOnlyList<MateriaCandidate> candidates)
        => this.candidates = candidates;

    public override void Draw()
    {
        if (ImGui.Button("Refresh"))
            this.Refresh();

        ImGui.SameLine();
        ImGui.TextUnformatted($"{this.candidates.Count} eligible item(s)");

        var filterChanged = false;
        filterChanged |= ImGui.RadioButton("Non-gearset items", !this.showGearsetItems);
        ImGui.SameLine();
        filterChanged |= ImGui.RadioButton("Gearset items", this.showGearsetItems);
        if (filterChanged)
        {
            this.showGearsetItems = !this.showGearsetItems;
            this.Refresh();
        }

        ImGui.Separator();

        if (ImGui.BeginTable("##MateriaRetreiveItems", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new(0, 260)))
        {
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Materia");
            ImGui.TableSetupColumn("Gearset");
            ImGui.TableSetupColumn("Container");
            ImGui.TableSetupColumn("Slot");
            ImGui.TableHeadersRow();

            foreach (var candidate in this.candidates)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(candidate.Name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(candidate.MateriaCount.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(candidate.IsInGearset ? "Yes" : "No");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(candidate.Container.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted((candidate.Slot + 1).ToString());
            }

            ImGui.EndTable();
        }

        var busy = this.queue.Count > 0;
        if (busy)
            ImGui.BeginDisabled();

        if (ImGui.Button("Begin retrieval", new(130, 0)))
            this.startRetrieval(this.candidates);

        if (busy)
            ImGui.EndDisabled();

        if (busy)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel queue", new(120, 0)))
                this.cancelRetrieval();
        }

        if (this.queue.Count > 0 || this.finished.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted($"Queued: {this.queue.Count}    Finished: {this.finished.Count}");
            if (this.finished.Count > 0)
            {
                ImGui.SameLine();
                if (ImGui.Button("Clear finished", new(120, 0)))
                    this.finished.Clear();
            }

            foreach (var item in this.queue.Take(5))
                ImGui.BulletText($"{item.Name}: {item.StartingMateriaCount - item.CurrentMateriaCount} / {item.StartingMateriaCount}");
        }
    }

    public void Dispose()
    {
    }
}
