# The island on a Windows CE budget

The dispenser side of the simulator (`GasPump.Dispenser`, the island) is the part that would live on a pump head. The course in MMP.AsyncProxy puts it on a Windows CE 6 board with the .NET Compact Framework 3.5, 64 MB of RAM, and a thread pool of 25. This page checks the island's thread and memory spending against that budget, says which numbers were measured here and which come from the CE and CF documentation, and names what a real port still has to verify on a device.

## The budget, from the documentation

| Constraint | Value | Source |
|---|---|---|
| Thread pool | 25 threads, 128 KB stack each, from CF 2.0 on; `SetMaxThreads` can raise it | [Threading in the .NET Compact Framework](https://learn.microsoft.com/en-us/previous-versions/dotnet/netframework-3.0/ms172522(v=vs.85)) |
| Default thread stack | 64 KB typical, with 8 KB reserved for overflow control; set per module by the linker and per thread by `CreateThread` | [Windows Embedded CE 6.0 stack docs](https://learn.microsoft.com/en-us/previous-versions/windows/embedded/ee483001(v=winembedded.60)) |
| Process address space | 1 GB for user code, data, heap, and stacks on CE 6 (CE 5 gave 32 MB) | [Virtual Memory Layout: CE 5.0 vs CE 6.0](https://msdn2.microsoft.com/en-us/library/aa914933(v=msdn.10)) |
| RAM | 64 MB on the board in the story. A board is what it is. | the course, `docs/course/00-dinner.md` in MMP.AsyncProxy |
| `ExecutionContext`, `SynchronizationContext` | Availability on CF 3.5 is unverified here. A third-party package adds `SynchronizationContext` to CF 3.5, which suggests it is missing. The library's builder and awaiter use both. | [TaskParallelLibraryCE](https://github.com/skarllot/TaskParallelLibraryCE) |

The pool's worst case is 25 threads times 128 KB, about 3.2 MB of stack. Every dedicated thread the island keeps costs its own stack on top of that, 64 KB by default.

## What the island spends under the AsyncProxy strategy

Counted from the source (`DispenserServer`, `AsyncProxyDispatch`):

| Spend | Count | What it is |
|---|---|---|
| Dedicated threads | 2 | the main thread, parked on the exit event, and `dispenser-accept`, which accepts the station and reads its frames |
| Workers | 1 | the pump worker: an iterator machine, a few hundred bytes, no thread while the mailbox is empty |
| Timers | 2 | the pump tick (100 ms) and the heartbeat; each fires on a pool thread for microseconds |
| Mailbox | 1 | capacity 1024 envelopes; a `Queue<T>` that holds references, so about 4 KB on a 32-bit box when full |
| Pool threads busy at once | 1 to 3 | a tick, a heartbeat, and the worker's wake-up can overlap; nothing else queues to the pool |
| Garbage per tick | about 1 KB | one promise, one future, one `Observe` wrapper per wake-up, measured in the training bench of MMP.AsyncProxy; ten ticks a second is about 10 KB a second |

Under the threaded strategy the pump worker holds a thread of its own, parked in `BlockingCollection.Take`, so the dedicated count is 3.

## Measured here, on .NET 10

The full Playwright suite (17 specs: sales, faults, the E-stop, prices and totals, the layout) was run twice on 2026-11-02 with the dispenser process sampled from outside every 250 ms. In the AsyncProxy run the dispenser's pool was capped at 25 workers and 25 I/O threads through `GASPUMP_DISPENSER_POOL_MAX=25`, and the dispenser's own startup line confirmed the cap took on a 24-processor machine.

```
GASPUMP_E2E_STRATEGY=asyncproxy GASPUMP_DISPENSER_POOL_MAX=25 npm run e2e      17 passed (3.5m)
[dispenser] thread pool cap 25 requested, set: max workers 25, io 25, processors 24
```

| Strategy | Pool cap | Specs | Dispenser threads, min / peak | Peak private bytes | Peak working set |
|---|---|---|---|---|---|
| asyncproxy | 25 | 17 of 17 | 11 / 16 | 34,234,368 | 75,698,176 |
| threaded | none | 17 of 17 | 11 / 16 | 33,320,960 | 75,304,960 |

The sampler counted every dispenser process the suite started; one spec kills the dispenser mid-fuel and the host starts it again.

The two rows match, and that is the right result for one pump. The island has one worker. Under threads that worker is one parked thread; under AsyncProxy it is one object, and the pool thread that runs its wake-ups was already there for the timers. One thread is inside the sampling noise. The difference is per worker, so it shows up when workers multiply: the training bench in MMP.AsyncProxy seats a thousand of them and the thread column reads 1018 against 34. A pump head with two nozzles, a printer, and a loyalty link is five or six workers, and under threads each one is a stack the board pays for while it waits.

How to read the thread column. The .NET 10 runtime itself holds about ten threads in an idle process: the GC, the finalizer, the tiered compiler, the timer queue, and the pool's minimum. The island's own spend is the difference above that, and under AsyncProxy it stayed inside the cap with room to spare while the suite ran a sale to the ceiling, a dispenser crash and restart, and a price change mid-delivery.

What does not transfer. The working set and private bytes are the .NET 10 runtime on Windows 11: Kestrel is not in this process, but System.Text.Json, Herald.OSS, and the TesseraSeal chain are, and none of them exist on the Compact Framework. The CF runtime's own footprint is a device measurement nobody has made yet. The numbers that do transfer are the counts in the table above: two dedicated threads, one worker with no thread, two timers, one bounded mailbox, and about a kilobyte of garbage per tick.

## The arithmetic for the board

Take the counts and the documented costs, on a 32-bit CE 6 box with CF 3.5:

| Item | Bytes |
|---|---|
| 2 dedicated threads at 64 KB | 131,072 |
| pool, worst case, 25 at 128 KB | 3,276,800 |
| pool, likely, 3 at 128 KB | 393,216 |
| mailbox at capacity, 1024 references | 4,096 |
| worker machine, promise, pending future | under 1,024 |
| pump controller state, ledger, nonces | under 65,536 by the source; measure on device |
| garbage per second at 10 ticks | 10,240, collected |

The island's own spend is under 4 MB in the worst case and under 1 MB in the likely case, on a 64 MB board. The runtime, the display, the card reader, and whatever else the head runs are the rest of the budget, and the island leaves them most of it.

## What a port still has to verify

1. `ExecutionContext` and `SynchronizationContext` on CF 3.5. If either is missing, `AsyncMethodBuilder<T>.AwaitOnCompleted` and `FutureAwaiter<T>.OnCompleted` need a `#if` path that skips the capture. The course's chapter 5 already tells Kevin to carry the sale in the object for that reason.
2. `System.Threading.Timer` resolution on the device. One report puts the CF 3.5 timer too coarse for a 50 ms interval on CE 7; the island ticks at 100 ms and the taper math is tolerant of jitter, but the number has to come from the board.
3. `TcpListener` and the blocking read on the accept thread. CF has both. The alternative, `BeginReceive` through `Futures.FromAsync`, would make the accept thread a pool callback and take the dedicated count to 1.
4. The CF runtime's footprint and the GC's behaviour under 10 KB a second of short-lived garbage. The CF collector is a simple mark-and-sweep with no generations, so the measurement is pause time at the tick rate.

## Run it yourself

```
cd app
GASPUMP_E2E_STRATEGY=asyncproxy GASPUMP_DISPENSER_POOL_MAX=25 npm run e2e
```

`GASPUMP_E2E_STRATEGY` picks the dispatch for the whole suite (`threaded`, `channel`, `asyncproxy`). `GASPUMP_DISPENSER_POOL_MAX` is read by `GasPump.Dispenser` at startup and applied with `ThreadPool.SetMaxThreads`; the cap cannot go below the processor count, and the dispenser logs what it got.
