using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using UmbraNightlife.Models;

namespace UmbraNightlife.Services;

/// <summary>
/// Thin wrapper around Lifestream's chat command <c>/li</c>. Detects availability
/// via IPC (<c>Lifestream.IsBusy</c>) and sends teleport commands through Dalamud's
/// command manager.
///
/// Availability is refreshed lazily on each call — Lifestream can be installed or
/// enabled at any time during the session.
/// </summary>
public sealed class LifestreamBridge : IDisposable
{
    private readonly IDalamudPluginInterface _pi;
    private readonly ICommandManager _commandManager;
    private readonly IChatGui _chat;
    private readonly IPluginLog _log;

    private ICallGateSubscriber<bool>? _isBusyGate;

    public LifestreamBridge(
        IDalamudPluginInterface pi,
        ICommandManager commandManager,
        IChatGui chat,
        IPluginLog log)
    {
        _pi = pi;
        _commandManager = commandManager;
        _chat = chat;
        _log = log;
    }

    /// <summary>Best-effort: probes IPC; returns true if Lifestream answers.</summary>
    public bool IsAvailable()
    {
        try
        {
            _isBusyGate ??= _pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
            _ = _isBusyGate.InvokeFunc();
            return true;
        }
        catch
        {
            _isBusyGate = null;
            return false;
        }
    }

    /// <summary>Sends <c>/li {address}</c>. Returns true if the command was queued.</summary>
    public bool TeleportTo(VenueView venue)
    {
        if (!IsAvailable())
        {
            _chat.PrintError("[Nightlife] Lifestream plugin is not installed or not responding.");
            return false;
        }

        try
        {
            if (_isBusyGate is not null && _isBusyGate.InvokeFunc())
            {
                _chat.PrintError("[Nightlife] Lifestream is busy — try again in a moment.");
                return false;
            }

            var command = $"/li {venue.LifestreamAddress}";
            _log.Info($"[Nightlife] {command}");
            _commandManager.ProcessCommand(command);
            _chat.Print($"[Nightlife] Teleporting to {venue.Name}…");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"[Nightlife] Lifestream teleport failed: {ex}");
            _chat.PrintError($"[Nightlife] Teleport failed: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _isBusyGate = null;
    }
}
