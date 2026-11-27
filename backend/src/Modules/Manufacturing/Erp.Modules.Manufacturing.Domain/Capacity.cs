namespace Erp.Modules.Manufacturing.Domain;

/// <summary>Working days of a work center as a bit mask: bit n = <see cref="DayOfWeek"/> n (Sunday = bit 0).</summary>
public static class WorkingDays
{
    public const int All = 0b111_1111;

    public static bool IsWorking(int mask, DateOnly date) => (mask & (1 << (int)date.DayOfWeek)) != 0;

    public static int FromNames(IEnumerable<string> names)
    {
        var mask = 0;
        foreach (var name in names)
        {
            if (!Enum.TryParse<DayOfWeek>(name, true, out var day))
            {
                throw new SharedKernel.Domain.DomainException(ManufacturingErrors.Invalid("manufacturing.working-days-invalid", $"Unknown day '{name}'."));
            }

            mask |= 1 << (int)day;
        }

        return mask;
    }

    public static IReadOnlyList<string> ToNames(int mask) =>
        Enum.GetValues<DayOfWeek>().Where(d => (mask & (1 << (int)d)) != 0).Select(d => d.ToString()).ToList();
}

public sealed record CapacityWorkCenter(Guid Id, string Code, decimal HoursPerDay, int WorkingDaysMask);

/// <summary>Remaining hours of one operation of an order (or planned order) on one work center.</summary>
public sealed record CapacityLoad(string SourceType, Guid SourceId, string Reference, Guid WorkCenterId, decimal Hours, DateOnly Start, DateOnly Due);

public sealed record CapacityBucket(Guid WorkCenterId, DateOnly Date, decimal Capacity, decimal Load)
{
    public bool Overloaded => Load > Capacity;
}

/// <summary>Result of finite loading for one order: the earliest date all its remaining hours fit, and whether that misses the due date.</summary>
public sealed record CapacityProposal(string SourceType, Guid SourceId, string Reference, DateOnly Due, DateOnly ProposedFinish, bool Late, bool Unschedulable);

public sealed record CapacityResult(IReadOnlyList<CapacityBucket> Buckets, IReadOnlyList<CapacityProposal> Proposals);

/// <summary>
/// Capacity check (pure). <b>Load</b>: each load's hours are spread evenly over the working days between its start and due date
/// (infinite loading, as MRP plans) and compared with the daily capacity. <b>Finite schedule</b>: loads are then placed in due-date
/// order into the earliest free capacity from their start date; an order finishes when its last load is placed, and is late when
/// that is after its due date — the proposed finish is the date to promise instead.
/// </summary>
public static class CapacityPlanner
{
    public const int MaxDays = 730;

    public static CapacityResult Plan(IReadOnlyCollection<CapacityWorkCenter> workCenters, IReadOnlyCollection<CapacityLoad> loads, DateOnly from, DateOnly to)
    {
        var centers = workCenters.ToDictionary(w => w.Id);
        var load = new Dictionary<(Guid, DateOnly), decimal>();
        foreach (var l in loads.Where(l => l.Hours > 0 && centers.ContainsKey(l.WorkCenterId)))
        {
            var wc = centers[l.WorkCenterId];
            var start = l.Start < from ? from : l.Start;
            var end = l.Due < start ? start : l.Due;
            var days = Days(start, end).Where(d => WorkingDays.IsWorking(wc.WorkingDaysMask, d)).ToList();
            if (days.Count == 0)
            {
                days = [NextWorkingDay(wc, start) ?? start];
            }

            var share = l.Hours / days.Count;
            foreach (var day in days)
            {
                load[(wc.Id, day)] = load.GetValueOrDefault((wc.Id, day)) + share;
            }
        }

        var buckets = new List<CapacityBucket>();
        foreach (var wc in centers.Values.OrderBy(w => w.Code, StringComparer.Ordinal))
        {
            foreach (var day in Days(from, to))
            {
                var capacity = WorkingDays.IsWorking(wc.WorkingDaysMask, day) ? wc.HoursPerDay : 0;
                var hours = MfgRounding.Quantity(load.GetValueOrDefault((wc.Id, day)));
                if (capacity > 0 || hours > 0)
                {
                    buckets.Add(new CapacityBucket(wc.Id, day, capacity, hours));
                }
            }
        }

        return new CapacityResult(buckets, Schedule(centers, loads, from));
    }

    private static List<CapacityProposal> Schedule(IReadOnlyDictionary<Guid, CapacityWorkCenter> centers, IReadOnlyCollection<CapacityLoad> loads, DateOnly from)
    {
        var used = new Dictionary<(Guid, DateOnly), decimal>();
        var proposals = new List<CapacityProposal>();
        foreach (var order in loads.Where(l => l.Hours > 0).GroupBy(l => (l.SourceType, l.SourceId))
                     .OrderBy(g => g.Min(l => l.Due)).ThenBy(g => g.Min(l => l.Start)).ThenBy(g => g.First().Reference, StringComparer.Ordinal))
        {
            var first = order.First();
            var due = order.Min(l => l.Due);
            var finish = from;
            var unschedulable = false;
            foreach (var l in order)
            {
                if (!centers.TryGetValue(l.WorkCenterId, out var wc) || wc.HoursPerDay <= 0 || wc.WorkingDaysMask == 0)
                {
                    unschedulable = true;
                    continue;
                }

                var day = l.Start < from ? from : l.Start;
                var left = l.Hours;
                var guard = 0;
                while (left > 0 && guard++ < MaxDays)
                {
                    if (WorkingDays.IsWorking(wc.WorkingDaysMask, day))
                    {
                        var free = wc.HoursPerDay - used.GetValueOrDefault((wc.Id, day));
                        if (free > 0)
                        {
                            var take = Math.Min(free, left);
                            used[(wc.Id, day)] = used.GetValueOrDefault((wc.Id, day)) + take;
                            left -= take;
                            if (left <= 0)
                            {
                                break;
                            }
                        }
                    }

                    day = day.AddDays(1);
                }

                unschedulable |= left > 0;
                if (day > finish)
                {
                    finish = day;
                }
            }

            proposals.Add(new CapacityProposal(first.SourceType, first.SourceId, first.Reference, due, finish, finish > due || unschedulable, unschedulable));
        }

        return proposals;
    }

    private static DateOnly? NextWorkingDay(CapacityWorkCenter wc, DateOnly start)
    {
        for (var i = 0; i < 7; i++)
        {
            if (WorkingDays.IsWorking(wc.WorkingDaysMask, start.AddDays(i)))
            {
                return start.AddDays(i);
            }
        }

        return null;
    }

    private static IEnumerable<DateOnly> Days(DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to && d.DayNumber - from.DayNumber <= MaxDays; d = d.AddDays(1))
        {
            yield return d;
        }
    }
}
