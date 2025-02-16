﻿using System.Numerics;
using Content.Server._RMC14.Marines;
using Content.Server.Doors.Systems;
using Content.Server.GameTicking;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Server.Shuttles.Systems;
using Content.Shared._RMC14.CCVar;
using Content.Shared._RMC14.Dropship;
using Content.Shared._RMC14.Marines;
using Content.Shared._RMC14.Marines.Announce;
using Content.Shared._RMC14.Rules;
using Content.Shared._RMC14.Xenonids;
using Content.Shared._RMC14.Xenonids.Announce;
using Content.Shared.Administration.Logs;
using Content.Shared.Database;
using Content.Shared.Doors.Components;
using Content.Shared.Interaction;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Robust.Server.Audio;
using Robust.Server.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._RMC14.Dropship;

public sealed class DropshipSystem : SharedDropshipSystem
{
    [Dependency] private readonly ISharedAdminLogManager _adminLog = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly IConfigurationManager _config = default!;
    [Dependency] private readonly DoorSystem _door = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly MarineAnnounceSystem _marineAnnounce = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly SharedXenoAnnounceSystem _xenoAnnounce = default!;

    private EntityQuery<DockingComponent> _dockingQuery;
    private EntityQuery<DoorComponent> _doorQuery;
    private EntityQuery<DoorBoltComponent> _doorBoltQuery;

    private TimeSpan _lzPrimaryAutoDelay;

    public override void Initialize()
    {
        base.Initialize();

        _dockingQuery = GetEntityQuery<DockingComponent>();
        _doorQuery = GetEntityQuery<DoorComponent>();
        _doorBoltQuery = GetEntityQuery<DoorBoltComponent>();

        SubscribeLocalEvent<DropshipNavigationComputerComponent, ActivateInWorldEvent>(OnActivateInWorld);

        SubscribeLocalEvent<DropshipComponent, FTLRequestEvent>(OnRefreshUI);
        SubscribeLocalEvent<DropshipComponent, FTLStartedEvent>(OnRefreshUI);
        SubscribeLocalEvent<DropshipComponent, FTLCompletedEvent>(OnFTLCompleted);
        SubscribeLocalEvent<DropshipComponent, FTLUpdatedEvent>(OnRefreshUI);

        Subs.BuiEvents<DropshipNavigationComputerComponent>(DropshipNavigationUiKey.Key,
            subs =>
            {
                subs.Event<DropshipLockdownMsg>(OnDropshipNavigationLockdownMsg);
            });

        Subs.CVar(_config, RMCCVars.RMCLandingZonePrimaryAutoMinutes, v => _lzPrimaryAutoDelay = TimeSpan.FromMinutes(v), true);
    }

    private void OnActivateInWorld(Entity<DropshipNavigationComputerComponent> ent, ref ActivateInWorldEvent args)
    {
        if (!HasComp<DropshipHijackerComponent>(args.User))
            return;

        if (TryComp(ent, out TransformComponent? xform) &&
            TryComp(xform.ParentUid, out DropshipComponent? dropship) &&
            dropship.Crashed)
        {
            return;
        }

        args.Handled = true;

        var destinations = new List<(NetEntity Id, string Name)>();
        var query = EntityQueryEnumerator<DropshipHijackDestinationComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            destinations.Add((GetNetEntity(uid), Name(uid)));
        }

        _ui.OpenUi(ent.Owner, DropshipHijackerUiKey.Key, args.User);
        _ui.SetUiState(ent.Owner, DropshipHijackerUiKey.Key, new DropshipHijackerBuiState(destinations));
    }

    private void OnFTLCompleted(Entity<DropshipComponent> ent, ref FTLCompletedEvent args)
    {
        OnRefreshUI(ent, ref args);

        if (HasComp<RMCPlanetComponent>(args.MapUid))
        {
            var ev = new DropshipLandedOnPlanetEvent();
            RaiseLocalEvent(ref ev);
        }

        if (HasComp<AlmayerComponent>(args.MapUid) && ent.Comp.Crashed)
        {
            var ev = new DropshipHijackLandedEvent();
            RaiseLocalEvent(ref ev);
        }
    }

    private void OnRefreshUI<T>(Entity<DropshipComponent> ent, ref T args)
    {
        RefreshUI();
    }

    private void OnDropshipNavigationLockdownMsg(Entity<DropshipNavigationComputerComponent> ent, ref DropshipLockdownMsg args)
    {
        if (_transform.GetGrid(ent.Owner) is not { } grid ||
            !TryComp(grid, out DropshipComponent? dropship) ||
            dropship.Crashed)
        {
            return;
        }

        if (TryComp(grid, out FTLComponent? ftl) &&
            ftl.State is FTLState.Travelling or FTLState.Arriving)
        {
            return;
        }

        var time = _timing.CurTime;
        if (time < dropship.LastLocked + dropship.LockCooldown)
            return;

        dropship.Locked = !dropship.Locked;
        dropship.LastLocked = time;
        SetAllDocks(grid, dropship.Locked);
    }

    public override bool FlyTo(Entity<DropshipNavigationComputerComponent> computer, EntityUid destination, EntityUid? user, bool hijack = false, float? startupTime = null, float? hyperspaceTime = null)
    {
        base.FlyTo(computer, destination, user, hijack, startupTime, hyperspaceTime);

        var shuttle = Transform(computer).GridUid;
        if (!TryComp(shuttle, out ShuttleComponent? shuttleComp))
        {
            Log.Warning($"Tried to launch {ToPrettyString(computer)} outside of a shuttle.");
            return false;
        }

        if (HasComp<FTLComponent>(shuttle))
        {
            Log.Warning($"Tried to launch shuttle {ToPrettyString(shuttle)} in FTL");
            return false;
        }

        var dropship = EnsureComp<DropshipComponent>(shuttle.Value);
        if (dropship.Crashed)
        {
            Log.Warning($"Tried to launch crashed dropship {ToPrettyString(shuttle.Value)}");
            return false;
        }

        if (dropship.Destination == destination)
        {
            Log.Warning($"Tried to launch {ToPrettyString(shuttle.Value)} to its current destination {ToPrettyString(destination)}.");
            return false;
        }

        if (TryComp(dropship.Destination, out DropshipDestinationComponent? oldDestination))
        {
            oldDestination.Ship = null;
            Dirty(dropship.Destination.Value, oldDestination);
        }

        if (TryComp(destination, out DropshipDestinationComponent? newDestination))
        {
            newDestination.Ship = shuttle;
            Dirty(destination, newDestination);
        }

        dropship.Destination = destination;
        Dirty(shuttle.Value, dropship);

        var destTransform = Transform(destination);
        var destCoords = _transform.GetMoverCoordinates(destination, destTransform);
        var rotation = destTransform.LocalRotation;
        if (TryComp(shuttle, out PhysicsComponent? physics))
            destCoords = destCoords.Offset(-physics.LocalCenter);

        destCoords = destCoords.Offset(new Vector2(-0.5f, -0.5f));
        _shuttle.FTLToCoordinates(shuttle.Value, shuttleComp, destCoords, rotation, startupTime: startupTime, hyperspaceTime: hyperspaceTime);

        if (user != null && hijack)
        {
            var xenoText = "The Queen has commanded the metal bird to depart for the metal hive in the sky! Rejoice!";
            _xenoAnnounce.AnnounceSameHive(user.Value, xenoText);
            _audio.PlayPvs(dropship.LocalHijackSound, shuttle.Value);

            var marineText = "Unscheduled dropship departure detected from operational area. Hijack likely. Shutting down autopilot.";
            _marineAnnounce.AnnounceRadio(shuttle.Value, marineText, dropship.AnnounceHijackIn);

            var marines = Filter.Empty().AddWhereAttachedEntity(e => !HasComp<XenoComponent>(e));
            _audio.PlayGlobal(dropship.MarineHijackSound, marines, true);
        }

        _adminLog.Add(LogType.RMCDropshipLaunch,
            $"{ToPrettyString(user):player} {(hijack ? "hijacked" : "launched")} {ToPrettyString(shuttle):dropship} to {ToPrettyString(destination):destination}");

        return true;
    }

    protected override void RefreshUI(Entity<DropshipNavigationComputerComponent> computer)
    {
        if (!_ui.IsUiOpen(computer.Owner, DropshipNavigationUiKey.Key))
            return;

        if (Transform(computer).GridUid is not { } grid)
            return;

        if (!TryComp(grid, out FTLComponent? ftl) ||
            !ftl.Running ||
            ftl.State == FTLState.Available)
        {
            var destinations = new List<Destination>();
            var query = EntityQueryEnumerator<DropshipDestinationComponent>();
            while (query.MoveNext(out var uid, out var comp))
            {
                var destination = new Destination(
                    GetNetEntity(uid),
                    Name(uid),
                    comp.Ship != null,
                    HasComp<PrimaryLandingZoneComponent>(uid)
                );
                destinations.Add(destination);
            }

            var state = new DropshipNavigationDestinationsBuiState(destinations);
            _ui.SetUiState(computer.Owner, DropshipNavigationUiKey.Key, state);
            return;
        }

        var destinationName = string.Empty;
        if (TryComp(grid, out DropshipComponent? dropship) &&
            dropship.Destination is { } destinationUid)
        {
            destinationName = Name(destinationUid);
        }
        else
        {
            Log.Error($"Found in-travel dropship {ToPrettyString(grid)} with invalid destination");
        }

        var travelState = new DropshipNavigationTravellingBuiState(ftl.State, ftl.StateTime, destinationName);
        _ui.SetUiState(computer.Owner, DropshipNavigationUiKey.Key, travelState);
    }

    protected override bool IsShuttle(EntityUid dropship)
    {
        return HasComp<ShuttleComponent>(dropship);
    }

    protected override bool IsInFTL(EntityUid dropship)
    {
        return HasComp<FTLComponent>(dropship);
    }

    protected override void RefreshUI()
    {
        var computers = EntityQueryEnumerator<DropshipNavigationComputerComponent>();
        while (computers.MoveNext(out var uid, out var comp))
        {
            RefreshUI((uid, comp));
        }
    }

    private void SetAllDocks(EntityUid dropship, bool locked)
    {
        var enumerator = Transform(dropship).ChildEnumerator;
        while (enumerator.MoveNext(out var child))
        {
            if (!_dockingQuery.HasComp(child))
                continue;

            if (locked)
                LockDoor(child);
            else
                UnlockDoor(child);
        }
    }

    public void LockDoor(Entity<DoorBoltComponent?> door)
    {
        if (_doorQuery.TryComp(door, out var doorComp) &&
            doorComp.State != DoorState.Closed)
        {
            var oldCheck = doorComp.PerformCollisionCheck;
            doorComp.PerformCollisionCheck = false;

            _door.StartClosing(door);
            _door.OnPartialClose(door);

            doorComp.PerformCollisionCheck = oldCheck;
        }

        if (_doorBoltQuery.Resolve(door, ref door.Comp, false))
            _door.SetBoltsDown((door.Owner, door.Comp), true);
    }

    public void UnlockDoor(Entity<DoorBoltComponent?> door)
    {
        if (_doorBoltQuery.Resolve(door, ref door.Comp, false))
            _door.SetBoltsDown((door.Owner, door.Comp), false);
    }

    public void RaiseUpdate(EntityUid shuttle)
    {
        var ev = new FTLUpdatedEvent();
        RaiseLocalEvent(shuttle, ref ev);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (Count<PrimaryLandingZoneComponent>() > 0)
            return;

        if (_gameTicker.RoundDuration() < _lzPrimaryAutoDelay)
            return;

        foreach (var primaryLZCandidate in GetPrimaryLZCandidates())
        {
            if (TryDesignatePrimaryLZ(default, primaryLZCandidate, new MarineCommunicationsComputerComponent().Sound))
                break;
        }
    }
}
