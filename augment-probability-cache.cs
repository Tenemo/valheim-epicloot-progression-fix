using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EpicLootProgressionFix
{
    internal static class AugmentProbabilityCache
    {
        private sealed class Calculation
        {
            internal string Key;
            internal float[] Weights;
            internal bool[] Removable;
            internal int Choices;
            internal bool Selected;
            internal readonly TaskCompletionSource<int[]> Completion = new TaskCompletionSource<int[]>();
            internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            internal LinkedListNode<Calculation> Pending;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Calculation> Results = new Dictionary<string, Calculation>();
        private static readonly LinkedList<Calculation> Pending = new LinkedList<Calculation>();
        private static bool running;
        private static Calculation active;

        // Only immutable numerical snapshots cross onto the worker. Selection requests take priority over prewarming.
        internal static Task<int[]> Get(string key, float[] weights, bool[] removable, int choices, bool priority)
        {
            lock (Gate)
            {
                Results.TryGetValue(key, out var existing);
                if (priority && active != null && active.Key != key && !active.Completion.Task.IsCompleted &&
                    (active.Selected || active.Choices != choices || existing == null || !existing.Completion.Task.IsCompleted))
                {
                    Results.Remove(active.Key);
                    active.Cancellation.Cancel();
                }
                if (existing != null)
                {
                    existing.Selected |= priority;
                    if (priority && existing.Pending != null)
                    {
                        Pending.Remove(existing.Pending);
                        existing.Pending = Pending.AddFirst(existing);
                    }
                    return existing.Completion.Task;
                }
                if (Pending.Count >= 64)
                {
                    if (!priority) return null;
                    var obsolete = Pending.Last.Value;
                    Pending.RemoveLast(); Results.Remove(obsolete.Key);
                    obsolete.Completion.SetCanceled();
                    obsolete.Cancellation.Dispose();
                }
                if (Results.Count >= 128)
                    Results.Remove(Results.First(pair => pair.Value.Completion.Task.IsCompleted).Key);
                var calculation = new Calculation { Key = key, Weights = (float[])weights.Clone(), Removable = (bool[])removable.Clone(), Choices = choices, Selected = priority };
                Results.Add(key, calculation);
                calculation.Pending = priority ? Pending.AddFirst(calculation) : Pending.AddLast(calculation);
                if (!running) { running = true; Task.Run(Work); }
                return calculation.Completion.Task;
            }
        }

        private static void Work()
        {
            while (true)
            {
                Calculation calculation;
                lock (Gate)
                {
                    if (Pending.Count == 0) { running = false; return; }
                    calculation = Pending.First.Value; Pending.RemoveFirst(); calculation.Pending = null; active = calculation;
                }
                try { calculation.Completion.SetResult(AugmentProbabilities.CalculateCancellable(calculation.Weights, calculation.Removable, calculation.Choices, calculation.Cancellation.Token)); }
                catch (OperationCanceledException) when (calculation.Cancellation.IsCancellationRequested) { calculation.Completion.SetCanceled(); }
                catch (Exception error)
                {
                    calculation.Completion.SetException(error);
                    // Retain the exception for the matching UI request, including failures from prewarming.
                    _ = calculation.Completion.Task.Exception;
                }
                finally
                {
                    lock (Gate) active = null;
                    calculation.Cancellation.Dispose();
                }
            }
        }

        internal static void Clear()
        {
            lock (Gate)
            {
                active?.Cancellation.Cancel();
                foreach (var calculation in Pending) { calculation.Completion.SetCanceled(); calculation.Cancellation.Dispose(); }
                Pending.Clear(); Results.Clear();
            }
        }
    }
}
