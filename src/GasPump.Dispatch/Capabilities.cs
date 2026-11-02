using GasPump.Core;

namespace GasPump.Dispatch;

/// <summary>
/// A listener on the station's telemetry: every sale event the sale worker
/// handled, and every view the dispenser sent. A capability only reads. It
/// can't write to the sale, the pump, or the bank. It has no return value and
/// no way to post a message back, which is why it is safe to bolt on.
///
/// A video that plays when fueling starts, a loyalty prompt, a forecourt
/// display, a metrics exporter: each is one of these.
/// </summary>
public interface IStationCapability
{
    string Name { get; }

    /// <summary>One sale event, after the sale machine has applied it. Runs on the capability's own worker.</summary>
    void OnSaleEvent(SaleEvent e);

    /// <summary>One view of the pump, as the dispenser sent it. Runs on the capability's own worker.</summary>
    void OnPumpView(DispenserView v) { }
}

/// <summary>
/// Runs each registered capability on its own worker behind its own bounded
/// mailbox, fed by <see cref="Publish(SaleEvent)"/> and <see cref="Publish(DispenserView)"/>.
///
/// Isolation comes from three rules. A full mailbox drops the message and
/// counts it, so a slow capability can't block the sale worker. A capability
/// that throws is switched off for the rest of the run and the log says so, so
/// a broken capability can't take the sale down with it. A capability receives
/// no handle to anything it could change.
/// </summary>
public sealed class CapabilityHost(IDispatchStrategy dispatch, IEventLog log)
{
    private sealed class Slot(IStationCapability capability, IMailbox<object> inbox)
    {
        public IStationCapability Capability => capability;
        public IMailbox<object> Inbox => inbox;
        public IWorkerHandle? Worker;
        public volatile bool Disabled;
        public long Dropped;
        public long Handled;
    }

    private readonly Lock _gate = new();
    private readonly List<Slot> _slots = [];

    public const int MailboxCapacity = 256;

    /// <summary>Attach a capability. Each gets its own worker named after it.</summary>
    public void Register(IStationCapability capability)
    {
        var slot = new Slot(capability, dispatch.CreateMailbox<object>($"capability:{capability.Name}", MailboxCapacity));
        slot.Worker = dispatch.StartWorker($"capability:{capability.Name}", slot.Inbox, m => Deliver(slot, m));
        lock (_gate) _slots.Add(slot);
        log.Write("capability", "{Name} registered", ("Name", capability.Name));
    }

    public void Publish(SaleEvent e) => Fan(e);
    public void Publish(DispenserView v) => Fan(v);

    /// <summary>Names of the capabilities that threw and were switched off.</summary>
    public IReadOnlyList<string> Disabled { get { lock (_gate) return _slots.Where(s => s.Disabled).Select(s => s.Capability.Name).ToArray(); } }

    /// <summary>Messages a capability missed because its mailbox was full.</summary>
    public long DroppedFor(string name) { lock (_gate) return _slots.FirstOrDefault(s => s.Capability.Name == name)?.Dropped ?? 0; }

    /// <summary>Messages a capability handled without throwing.</summary>
    public long HandledFor(string name) { lock (_gate) return _slots.FirstOrDefault(s => s.Capability.Name == name)?.Handled ?? 0; }

    private void Fan(object message)
    {
        Slot[] slots;
        lock (_gate) slots = _slots.ToArray();
        foreach (var slot in slots)
        {
            if (slot.Disabled) continue;
            if (slot.Inbox.TryPost(message)) continue;
            // Never wait on a capability. The sale worker is serving a driver at the hose.
            var dropped = Interlocked.Increment(ref slot.Dropped);
            if (dropped == 1) log.Warn("capability", "{Name} mailbox full, dropping telemetry", ("Name", slot.Capability.Name));
        }
    }

    private void Deliver(Slot slot, object message)
    {
        if (slot.Disabled) return;
        try
        {
            switch (message)
            {
                case SaleEvent e: slot.Capability.OnSaleEvent(e); break;
                case DispenserView v: slot.Capability.OnPumpView(v); break;
            }
            Interlocked.Increment(ref slot.Handled);
        }
        catch (Exception ex)
        {
            slot.Disabled = true;
            log.Error("capability", "{Name} threw {Type}: {Message}; switched off for this run", ("Name", slot.Capability.Name), ("Type", ex.GetType().Name), ("Message", ex.Message));
        }
    }
}
