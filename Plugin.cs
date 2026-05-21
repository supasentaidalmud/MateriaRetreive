using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using MateriaRetreive.Windows;

namespace MateriaRetreive;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string CommandName = "/mr";
    private const ushort FullySpiritbound = 10000;

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly BulkRetrieveWindow bulkRetrieveWindow;
    private readonly Queue<QueuedMateriaItem> queue = new();
    private readonly List<QueuedMateriaItem> finished = [];
    private bool updateSubscribed;

    public WindowSystem WindowSystem { get; } = new("MateriaRetreive");

    public Plugin()
    {
        this.bulkRetrieveWindow = new BulkRetrieveWindow(this.ScanCandidates, this.StartRetrieval, this.CancelRetrieval, this.queue, this.finished);
        this.WindowSystem.AddWindow(this.bulkRetrieveWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open MateriaRetreive.",
            ShowInHelp = true,
        });

        ContextMenu.OnMenuOpened += this.OnMenuOpened;
        PluginInterface.UiBuilder.Draw += this.WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += this.OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi += this.OpenWindow;
    }

    public void Dispose()
    {
        this.UnsubscribeUpdate();
        PluginInterface.UiBuilder.OpenConfigUi -= this.OpenWindow;
        PluginInterface.UiBuilder.OpenMainUi -= this.OpenWindow;
        PluginInterface.UiBuilder.Draw -= this.WindowSystem.Draw;
        ContextMenu.OnMenuOpened -= this.OnMenuOpened;
        CommandManager.RemoveHandler(CommandName);
        this.WindowSystem.RemoveAllWindows();
        this.bulkRetrieveWindow.Dispose();
    }

    private void OnCommand(string command, string args) => this.OpenWindow();

    private void OpenWindow()
    {
        this.bulkRetrieveWindow.Refresh();
        this.bulkRetrieveWindow.IsOpen = true;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Inventory)
            return;

        var targetItem = GetMenuTargetItem(args);
        if (targetItem == null)
            return;

        var candidate = this.CreateCandidate(targetItem, targetItem->Container, targetItem->Slot, false);
        if (candidate is null)
            return;

        args.AddMenuItem(new MenuItem
        {
            Name = candidate.CanExtractMateria ? "Retreive Materia" : "Retreive All Materia",
            Prefix = SeIconChar.Circle,
            PrefixColor = 37,
            Priority = candidate.CanExtractMateria ? 0 : 100,
            OnClicked = _ =>
            {
                if (candidate.CanExtractMateria)
                    this.StartRetrieval([candidate]);
                else
                    this.OpenWindowForCandidate(candidate);
            },
        });
    }

    private void OpenWindowForCandidate(MateriaCandidate candidate)
    {
        this.bulkRetrieveWindow.SetCandidates([candidate]);
        this.bulkRetrieveWindow.IsOpen = true;
    }

    private IReadOnlyList<MateriaCandidate> ScanCandidates(bool showGearsetItems)
    {
        var assignedItemIds = this.GetAssignedGearsetItemIds();
        var candidates = new List<MateriaCandidate>();

        foreach (var inventoryType in InventoryTypesToScan)
        {
            var container = InventoryManager.Instance()->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
                continue;

            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                if (item == null || item->IsEmpty() || item->GetMateriaCount() == 0)
                    continue;

                var isInGearset = assignedItemIds.Contains(item->GetBaseItemId());
                if (isInGearset != showGearsetItems)
                    continue;

                if (this.CreateCandidate(item, inventoryType, slot, isInGearset) is { } candidate)
                    candidates.Add(candidate);
            }
        }

        return candidates
            .OrderBy(candidate => candidate.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(candidate => candidate.Container)
            .ThenBy(candidate => candidate.Slot)
            .ToArray();
    }

    private static InventoryItem* GetMenuTargetItem(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetInventory { TargetItem: { IsEmpty: false } targetItem })
            return null;

        var item = (InventoryItem*)targetItem.Address;
        if (item == null || item->IsEmpty() || item->GetMateriaCount() == 0)
            return null;

        return item;
    }

    private MateriaCandidate? CreateCandidate(InventoryItem* item, InventoryType inventoryType, int slot, bool isInGearset)
    {
        var baseItemId = item->GetBaseItemId();
        var itemRow = DataManager.Excel.GetSheet<Item>().GetRowOrDefault(baseItemId);
        if (itemRow is null || itemRow.Value.MateriaSlotCount == 0)
            return null;

        return new MateriaCandidate(
            item,
            itemRow.Value.Name.ExtractText(),
            baseItemId,
            item->GetMateriaCount(),
            item->GetSpiritbondOrCollectability() >= FullySpiritbound,
            inventoryType,
            slot,
            isInGearset);
    }

    private HashSet<uint> GetAssignedGearsetItemIds()
    {
        var assigned = new HashSet<uint>();
        var gearsets = RaptureGearsetModule.Instance();
        if (gearsets == null)
            return assigned;

        for (var gearsetId = 0; gearsetId < gearsets->Entries.Length; gearsetId++)
        {
            if (!gearsets->IsValidGearset(gearsetId))
                continue;

            var gearset = gearsets->GetGearset(gearsetId);
            if (gearset == null)
                continue;

            if (!gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
                continue;

            foreach (ref var item in gearset->Items)
            {
                if (item.ItemId != 0)
                    assigned.Add(item.ItemId % 1_000_000);
            }
        }

        return assigned;
    }

    private void StartRetrieval(IReadOnlyList<MateriaCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            ChatGui.Print("No listed melded items found.", "MateriaRetreive");
            return;
        }

        this.queue.Clear();
        this.finished.Clear();
        this.bulkRetrieveWindow.SetCandidates(candidates);

        foreach (var candidate in candidates)
            this.queue.Enqueue(new QueuedMateriaItem(candidate));

        this.bulkRetrieveWindow.IsOpen = true;
        this.SubscribeUpdate();
        Log.Information("Queued materia retrieval for {Count} item(s).", candidates.Count);
    }

    private void CancelRetrieval()
    {
        this.queue.Clear();
        this.UnsubscribeUpdate();
        ChatGui.Print("Materia retrieval queue cancelled.", "MateriaRetreive");
        Log.Information("Materia retrieval queue cancelled.");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (this.queue.Count == 0)
        {
            this.UnsubscribeUpdate();
            ChatGui.Print("Materia retrieval queue finished.", "MateriaRetreive");
            return;
        }

        if (Condition[ConditionFlag.Occupied39])
            return;

        var current = this.queue.Peek();
        switch (current.GetStatus())
        {
            case RetrievalAttemptStatus.NoAttemptMade:
            case RetrievalAttemptStatus.RetrievedSome:
            case RetrievalAttemptStatus.RetryNeeded:
                current.AttemptRetrieval();
                break;

            case RetrievalAttemptStatus.RetrievedAll:
                this.finished.Add(this.queue.Dequeue());
                break;

            case RetrievalAttemptStatus.AttemptRunning:
                break;

            case RetrievalAttemptStatus.TimedOut:
                ChatGui.PrintError($"Timed out retrieving materia from {current.Name}.", "MateriaRetreive");
                this.finished.Add(this.queue.Dequeue());
                break;
        }
    }

    private void SubscribeUpdate()
    {
        if (this.updateSubscribed)
            return;

        Framework.Update += this.OnFrameworkUpdate;
        this.updateSubscribed = true;
    }

    private void UnsubscribeUpdate()
    {
        if (!this.updateSubscribed)
            return;

        Framework.Update -= this.OnFrameworkUpdate;
        this.updateSubscribed = false;
    }

    private static readonly InventoryType[] InventoryTypesToScan =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
    ];
}

public sealed unsafe class QueuedMateriaItem(MateriaCandidate candidate)
{
    private readonly InventoryItem* item = candidate.Item;
    private readonly byte startingMateriaCount = candidate.MateriaCount;
    private byte previousMateriaCount = candidate.MateriaCount;
    private DateTime? lastAttemptAt;
    private byte attemptCount;

    public string Name => candidate.Name;
    public byte CurrentMateriaCount => this.item->GetMateriaCount();
    public byte StartingMateriaCount => this.startingMateriaCount;

    public RetrievalAttemptStatus GetStatus()
    {
        var currentCount = this.CurrentMateriaCount;
        if (currentCount == 0)
            return RetrievalAttemptStatus.RetrievedAll;

        if (this.lastAttemptAt is null)
            return RetrievalAttemptStatus.NoAttemptMade;

        if (this.attemptCount > 3)
            return RetrievalAttemptStatus.TimedOut;

        if (currentCount != this.previousMateriaCount)
            return RetrievalAttemptStatus.RetrievedSome;

        return this.lastAttemptAt.Value.AddSeconds(3) < DateTime.UtcNow
            ? RetrievalAttemptStatus.RetryNeeded
            : RetrievalAttemptStatus.AttemptRunning;
    }

    public void AttemptRetrieval()
    {
        this.previousMateriaCount = this.CurrentMateriaCount;
        this.lastAttemptAt = DateTime.UtcNow;
        this.attemptCount++;
        EventFramework.Instance()->MaterializeItem(this.item, MaterializeEntryId.Retrieve);
    }
}

public enum RetrievalAttemptStatus
{
    NoAttemptMade,
    RetrievedSome,
    RetrievedAll,
    AttemptRunning,
    RetryNeeded,
    TimedOut,
}

public unsafe sealed class MateriaCandidate(
    InventoryItem* item,
    string name,
    uint itemId,
    byte materiaCount,
    bool canExtractMateria,
    InventoryType container,
    int slot,
    bool isInGearset)
{
    public InventoryItem* Item { get; } = item;
    public string Name { get; } = name;
    public uint ItemId { get; } = itemId;
    public byte MateriaCount { get; } = materiaCount;
    public bool CanExtractMateria { get; } = canExtractMateria;
    public InventoryType Container { get; } = container;
    public int Slot { get; } = slot;
    public bool IsInGearset { get; } = isInGearset;
}
