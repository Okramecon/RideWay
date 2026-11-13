using GoHome.Web.Data;
using GoHome.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace GoHome.Web.Services;

public record OpResult(bool Ok, string Message)
{
    public static OpResult Success(string message) => new(true, message);
    public static OpResult Fail(string message) => new(false, message);
}

/// <summary>
/// Запись на маршрут, разовые поездки, «не еду» — с выбором конкретного места.
/// Все операции, меняющие занятость мест, идут под одной блокировкой —
/// пилот работает одним процессом, этого достаточно, чтобы не продать одно место дважды.
/// </summary>
public class BookingService(AppDbContext db, TripService trips, WorkCalendar calendar, IClock clock, AuditService audit)
{
    private static readonly SemaphoreSlim SeatLock = new(1, 1);

    /// <summary>Места маршрута, закреплённые за постоянными пассажирами хотя бы на одном из направлений.</summary>
    public async Task<HashSet<int>> RegularSeatsTakenAsync(int routeId, Direction[] dirs, int? excludeUserId = null)
    {
        var today = clock.Today;
        var ms = await db.Memberships
            .Where(m => m.RouteId == routeId && m.Status == MembershipStatus.Regular && m.SeatNumber != null
                        && (m.To == null || m.To >= today)
                        && (excludeUserId == null || m.UserId != excludeUserId))
            .ToListAsync();
        return ms.Where(m => dirs.Any(m.Covers)).Select(m => m.SeatNumber!.Value).ToHashSet();
    }

    public async Task<OpResult> JoinRouteAsync(int userId, int routeId, int stopId, bool morning, bool evening, string actor, int? seat = null)
    {
        if (!morning && !evening) return OpResult.Fail("Выберите утро, вечер или оба направления.");

        await SeatLock.WaitAsync();
        try
        {
            var route = await db.Routes.Include(r => r.Car).Include(r => r.Stops).SingleOrDefaultAsync(r => r.Id == routeId);
            if (route is null || !route.IsActive) return OpResult.Fail("Маршрут недоступен.");
            if (route.Stops.All(s => s.Id != stopId)) return OpResult.Fail("Остановка не относится к маршруту.");

            var from = calendar.UpcomingChangeableDays(1).FirstOrDefault();
            if (from == default) return OpResult.Fail("Нет ближайших рабочих дней.");

            var dirs = Dirs(morning, evening);
            var pick = await PickSeatAsync(route, dirs, from, userId, seat);
            if (pick.Error is not null) return OpResult.Fail(pick.Error);

            // Старая запись (другой маршрут, остановка или место) закрывается.
            var active = await db.Memberships
                .Where(m => m.UserId == userId && m.Status != MembershipStatus.Left).ToListAsync();
            foreach (var old in active)
            {
                old.Status = MembershipStatus.Left;
                old.To = from.AddDays(-1);
            }

            var membership = new Membership
            {
                UserId = userId, RouteId = routeId, StopId = stopId, Morning = morning, Evening = evening,
                From = from, CreatedAt = clock.UtcNow,
            };

            if (pick.Seat is { } chosen)
            {
                // Место на ближайшие дни может быть занято чужой разовой бронью — тогда стартуем позже.
                membership.From = pick.Start;
                membership.SeatNumber = chosen;
                membership.Status = MembershipStatus.Regular;
                await CancelOwnOneOffsAsync(userId, routeId, dirs, membership.From);
            }
            else
            {
                membership.Status = MembershipStatus.Waitlist;
            }

            db.Memberships.Add(membership);
            await db.SaveChangesAsync();
            await audit.LogAsync("Membership", membership.Id, "Join",
                $"route={routeId} stop={stopId} seat={membership.SeatNumber} status={membership.Status} from={membership.From}", actor);

            var stop = route.Stops.Single(s => s.Id == stopId);
            return membership.Status == MembershipStatus.Regular
                ? OpResult.Success($"Вы записаны на «{route.Name}», остановка «{stop.Name}», место {membership.SeatNumber}, с {WorkCalendar.Format(membership.From)}.")
                : OpResult.Success($"Мест на «{route.Name}» сейчас нет — вы в очереди ожидания. Сообщим, когда место освободится.");
        }
        finally
        {
            SeatLock.Release();
        }
    }

    /// <summary>Перевести из очереди в постоянные (если есть место).</summary>
    public async Task<OpResult> PromoteAsync(int membershipId, string actor, int? seat = null)
    {
        await SeatLock.WaitAsync();
        try
        {
            var m = await db.Memberships.Include(x => x.Route).ThenInclude(r => r.Car)
                .SingleAsync(x => x.Id == membershipId);
            if (m.Status != MembershipStatus.Waitlist) return OpResult.Fail("Запись не в очереди.");

            var from = calendar.UpcomingChangeableDays(1).FirstOrDefault();
            var dirs = Dirs(m.Morning, m.Evening);
            var pick = await PickSeatAsync(m.Route, dirs, from, m.UserId, seat);
            if (pick.Error is not null) return OpResult.Fail(pick.Error);
            if (pick.Seat is not { } chosen) return OpResult.Fail("На маршруте нет свободных постоянных мест.");

            m.From = pick.Start;
            m.SeatNumber = chosen;
            m.Status = MembershipStatus.Regular;
            await CancelOwnOneOffsAsync(m.UserId, m.RouteId, dirs, m.From);
            await db.SaveChangesAsync();
            await audit.LogAsync("Membership", m.Id, "Promote", $"seat={chosen} from={m.From}", actor);
            return OpResult.Success($"Место {chosen} закреплено с {WorkCalendar.Format(m.From)}.");
        }
        finally
        {
            SeatLock.Release();
        }
    }

    /// <summary>Сменить место и/или остановку у постоянного пассажира без перезаписи на маршрут.</summary>
    public async Task<OpResult> ChangeSeatAsync(int userId, int? seat, int? stopId, string actor)
    {
        await SeatLock.WaitAsync();
        try
        {
            var m = await db.Memberships.Include(x => x.Route).ThenInclude(r => r.Car).Include(x => x.Route.Stops)
                .FirstOrDefaultAsync(x => x.UserId == userId && x.Status == MembershipStatus.Regular && x.To == null);
            if (m is null) return OpResult.Fail("У вас нет постоянного места.");

            if (stopId is { } sid)
            {
                if (m.Route.Stops.All(s => s.Id != sid)) return OpResult.Fail("Остановка не относится к маршруту.");
                m.StopId = sid;
            }

            if (seat is { } wanted && wanted != m.SeatNumber)
            {
                var from = calendar.UpcomingChangeableDays(1).FirstOrDefault();
                if (from == default) from = clock.Today;
                var pick = await PickSeatAsync(m.Route, Dirs(m.Morning, m.Evening), from, userId, wanted);
                if (pick.Error is not null) return OpResult.Fail(pick.Error);
                if (pick.Start > from) return OpResult.Fail($"На место {wanted} уже есть разовые брони на ближайшие дни — выберите другое.");
                m.SeatNumber = wanted;
            }

            await db.SaveChangesAsync();
            await audit.LogAsync("Membership", m.Id, "ChangeSeat", $"seat={m.SeatNumber} stop={m.StopId}", actor);
            return OpResult.Success($"Сохранено: место {m.SeatNumber}.");
        }
        finally
        {
            SeatLock.Release();
        }
    }

    public async Task<OpResult> LeaveRouteAsync(int userId, string actor)
    {
        var active = await db.Memberships.Where(m => m.UserId == userId && m.Status != MembershipStatus.Left).ToListAsync();
        if (active.Count == 0) return OpResult.Fail("Вы не записаны на маршрут.");
        var lastDay = clock.Today;
        foreach (var m in active)
        {
            m.Status = MembershipStatus.Left;
            m.To = lastDay;
            await audit.LogAsync("Membership", m.Id, "Leave", null, actor);
        }
        await db.SaveChangesAsync();
        return OpResult.Success("Вы больше не записаны на маршрут.");
    }

    public async Task<OpResult> BookOneOffAsync(int userId, int routeId, int stopId, DateOnly date, Direction dir, string actor, int? seat = null)
    {
        if (!calendar.IsWorkday(date)) return OpResult.Fail("В этот день рейсов нет.");
        if (!calendar.CanChange(date)) return OpResult.Fail("Запись на этот рейс уже закрыта.");
        if (date > clock.Today.AddDays(14)) return OpResult.Fail("Слишком далеко вперёд.");

        await SeatLock.WaitAsync();
        try
        {
            var stopOk = await db.Stops.AnyAsync(s => s.Id == stopId && s.RouteId == routeId);
            if (!stopOk) return OpResult.Fail("Остановка не относится к маршруту.");

            if (await RidesAnywhereAsync(userId, date, dir))
                return OpResult.Fail("У вас уже есть место на этот рейс.");

            var roster = await trips.GetRosterAsync(routeId, date, dir);
            if (!roster.Runs) return OpResult.Fail("Этот рейс не выполняется.");
            if (roster.Free <= 0) return OpResult.Fail("Свободных мест нет.");

            var free = roster.FreeSeatNumbers;
            if (seat is { } wanted)
            {
                if (!roster.Layout.HasSeat(wanted)) return OpResult.Fail("Такого места нет в машине.");
                if (!free.Contains(wanted)) return OpResult.Fail("Это место уже занято — выберите другое.");
            }
            else if (free.Count == 0)
            {
                return OpResult.Fail("Свободных мест нет.");
            }
            var chosen = seat ?? free.Min();

            var booking = new OneOffBooking
            {
                UserId = userId, RouteId = routeId, StopId = stopId, Date = date, Direction = dir,
                SeatNumber = chosen, Status = BookingStatus.Booked, CreatedAt = clock.UtcNow,
            };
            db.OneOffBookings.Add(booking);
            await db.SaveChangesAsync();
            await audit.LogAsync("OneOffBooking", booking.Id, "Book", $"{date} {dir} seat={chosen}", actor);
            return OpResult.Success($"Место {chosen} на {WorkCalendar.Format(date)}, {WorkCalendar.Format(dir)} забронировано.");
        }
        finally
        {
            SeatLock.Release();
        }
    }

    public async Task<OpResult> CancelOneOffAsync(int bookingId, int userId, string actor)
    {
        var b = await db.OneOffBookings.SingleOrDefaultAsync(x => x.Id == bookingId && x.UserId == userId);
        if (b is null || b.Status != BookingStatus.Booked) return OpResult.Fail("Бронь не найдена.");
        if (!calendar.CanChange(b.Date)) return OpResult.Fail("Отменить уже нельзя — срок прошёл.");
        b.Status = BookingStatus.Cancelled;
        await db.SaveChangesAsync();
        await audit.LogAsync("OneOffBooking", b.Id, "Cancel", null, actor);
        return OpResult.Success("Разовая поездка отменена.");
    }

    public async Task<OpResult> MarkAbsentAsync(int userId, DateOnly date, Direction dir, string actor)
    {
        if (!calendar.CanChange(date)) return OpResult.Fail("Отметить уже нельзя — срок прошёл (накануне до 21:00).");

        var oneOff = await db.OneOffBookings.FirstOrDefaultAsync(b =>
            b.UserId == userId && b.Date == date && b.Direction == dir && b.Status == BookingStatus.Booked);
        if (oneOff is not null) return await CancelOneOffAsync(oneOff.Id, userId, actor);

        var regular = await RegularMembershipAsync(userId, date, dir);
        if (regular is null) return OpResult.Fail("У вас нет места на этот рейс.");

        if (!await db.Absences.AnyAsync(a => a.UserId == userId && a.Date == date && a.Direction == dir))
        {
            var a = new Absence { UserId = userId, Date = date, Direction = dir, CreatedAt = clock.UtcNow };
            db.Absences.Add(a);
            await db.SaveChangesAsync();
            await audit.LogAsync("Absence", a.Id, "Add", $"{date} {dir}", actor);
        }
        return OpResult.Success($"Отмечено: {WorkCalendar.Format(date)}, {WorkCalendar.Format(dir)} — не едете.");
    }

    public async Task<OpResult> UnmarkAbsentAsync(int userId, DateOnly date, Direction dir, string actor)
    {
        if (!calendar.CanChange(date)) return OpResult.Fail("Изменить уже нельзя — срок прошёл.");

        await SeatLock.WaitAsync();
        try
        {
            var absence = await db.Absences.SingleOrDefaultAsync(a => a.UserId == userId && a.Date == date && a.Direction == dir);
            if (absence is null) return OpResult.Fail("Отметки «не еду» нет.");

            var regular = await RegularMembershipAsync(userId, date, dir);
            if (regular is null) return OpResult.Fail("У вас нет места на этот рейс.");

            // Пока пассажир отсутствовал, его место могли занять разовой поездкой.
            var roster = await trips.GetRosterAsync(regular.RouteId, date, dir);
            var seatTaken = regular.SeatNumber is { } own
                ? roster.Entries.Any(e => e.TakesSeat && e.UserId != userId && e.SeatNumber == own)
                : roster.Free <= 0;
            if (seatTaken) return OpResult.Fail("Ваше место на этот рейс уже занято разовой поездкой.");

            db.Absences.Remove(absence);
            await db.SaveChangesAsync();
            await audit.LogAsync("Absence", absence.Id, "Remove", $"{date} {dir}", actor);
            return OpResult.Success($"Вы снова едете {WorkCalendar.Format(date)}, {WorkCalendar.Format(dir)}.");
        }
        finally
        {
            SeatLock.Release();
        }
    }

    private Task<Membership?> RegularMembershipAsync(int userId, DateOnly date, Direction dir) =>
        db.Memberships.Where(m => m.UserId == userId && m.Status == MembershipStatus.Regular
                                  && m.From <= date && (m.To == null || m.To >= date)
                                  && (dir == Direction.Morning ? m.Morning : m.Evening))
            .FirstOrDefaultAsync();

    private async Task<bool> RidesAnywhereAsync(int userId, DateOnly date, Direction dir)
    {
        if (await db.OneOffBookings.AnyAsync(b => b.UserId == userId && b.Date == date && b.Direction == dir && b.Status == BookingStatus.Booked))
            return true;
        if (await RegularMembershipAsync(userId, date, dir) is null) return false;
        return !await db.Absences.AnyAsync(a => a.UserId == userId && a.Date == date && a.Direction == dir);
    }

    private record Pick(int? Seat, DateOnly Start, string? Error);

    /// <summary>
    /// Выбор места для постоянной записи. Свободное место — то, которое ни у кого из постоянных пассажиров
    /// не занято на нужных направлениях. Если оно уже занято чужой разовой бронью в ближайшие недели,
    /// стартуем со дня после последней такой брони. Seat == null и Error == null — мест нет (очередь).
    /// </summary>
    private async Task<Pick> PickSeatAsync(Route route, Direction[] dirs, DateOnly from, int userId, int? preferred)
    {
        var layout = route.Car.ParsedLayout();
        var taken = await RegularSeatsTakenAsync(route.Id, dirs, userId);

        if (preferred is { } p)
        {
            if (!layout.HasSeat(p)) return new Pick(null, from, "Такого места нет в машине.");
            if (taken.Contains(p)) return new Pick(null, from, "Это место уже занято — выберите другое.");
        }

        var free = layout.SeatNumbers.Where(n => !taken.Contains(n)).ToList();
        if (free.Count == 0) return new Pick(null, from, null);

        var horizon = from.AddDays(21);
        var oneOffs = await db.OneOffBookings
            .Where(b => b.RouteId == route.Id && b.UserId != userId && b.Status == BookingStatus.Booked
                        && b.SeatNumber != null && b.Date >= from && b.Date <= horizon)
            .ToListAsync();

        DateOnly StartFor(int seat)
        {
            var start = from;
            foreach (var b in oneOffs.Where(b => b.SeatNumber == seat && dirs.Contains(b.Direction)))
                if (b.Date >= start) start = b.Date.AddDays(1);
            while (!calendar.IsWorkday(start)) start = start.AddDays(1);
            return start;
        }

        var chosen = preferred ?? free.OrderBy(StartFor).ThenBy(n => n).First();
        return new Pick(chosen, StartFor(chosen), null);
    }

    private async Task CancelOwnOneOffsAsync(int userId, int routeId, Direction[] dirs, DateOnly from)
    {
        var own = await db.OneOffBookings.Where(b => b.UserId == userId && b.RouteId == routeId
                                                     && b.Date >= from && b.Status == BookingStatus.Booked).ToListAsync();
        foreach (var b in own.Where(b => dirs.Contains(b.Direction)))
            b.Status = BookingStatus.Cancelled;
    }

    private static Direction[] Dirs(bool morning, bool evening) =>
        [.. (morning ? [Direction.Morning] : Array.Empty<Direction>()), .. (evening ? [Direction.Evening] : Array.Empty<Direction>())];
}
