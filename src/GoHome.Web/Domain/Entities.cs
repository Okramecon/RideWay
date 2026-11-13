namespace GoHome.Web.Domain;

public enum Direction
{
    /// <summary>Утро: из дома в офис.</summary>
    Morning = 0,
    /// <summary>Вечер: из офиса домой.</summary>
    Evening = 1,
}

public enum UserRole
{
    Passenger = 0,
    Driver = 1,
    Admin = 2,
}

public enum MembershipStatus
{
    /// <summary>Постоянный пассажир, место закреплено.</summary>
    Regular = 0,
    /// <summary>В очереди ожидания — мест не было.</summary>
    Waitlist = 1,
    /// <summary>Перестал ездить.</summary>
    Left = 2,
}

public enum BookingStatus
{
    Booked = 0,
    Cancelled = 1,
}

public enum RideResult
{
    Boarded = 0,
    NoShow = 1,
}

public enum PaymentMethod
{
    Cash = 0,
    Transfer = 1,
}

public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Только цифры, например 996555123456.</summary>
    public string Phone { get; set; } = "";
    public long? TelegramId { get; set; }
    public UserRole Role { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Car
{
    public int Id { get; set; }
    public string Model { get; set; } = "";
    public string Plate { get; set; } = "";
    /// <summary>Пассажирских мест без водителя.</summary>
    public int Seats { get; set; }
    public int? DriverUserId { get; set; }
    public User? Driver { get; set; }
    /// <summary>Секрет QR-наклейки в салоне: пассажир сканирует его при посадке.</summary>
    public string BoardingCode { get; set; } = "";
    /// <summary>Схема мест салона (формат — в SeatLayout). Пусто — типовая схема по числу мест.</summary>
    public string Layout { get; set; } = "";
    /// <summary>Имя файла фото салона в хранилище (отдаётся как /media/&lt;имя&gt;).</summary>
    public string? CabinPhoto { get; set; }

    public SeatLayout ParsedLayout() =>
        SeatLayout.TryParse(Layout, out var layout, out _) ? layout! : SeatLayout.Default(Seats);

    public static string NewBoardingCode() =>
        Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6));
}

public class Route
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int CarId { get; set; }
    public Car Car { get; set; } = null!;
    public decimal PricePerRide { get; set; }
    public bool IsActive { get; set; } = true;
    public List<Stop> Stops { get; set; } = [];
    /// <summary>Линия маршрута по дорогам: JSON-массив [[lat,lng],…]. Пусто — рисуем прямые между остановками.</summary>
    public string? PathJson { get; set; }
}

public class Stop
{
    public int Id { get; set; }
    public int RouteId { get; set; }
    public Route Route { get; set; } = null!;
    public int Order { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Время посадки утром (в сторону офиса).</summary>
    public TimeOnly MorningTime { get; set; }
    /// <summary>Время высадки вечером (из офиса).</summary>
    public TimeOnly EveningTime { get; set; }
    /// <summary>Точное место остановки на карте. Null — ещё не поставлено.</summary>
    public double? Lat { get; set; }
    public double? Lng { get; set; }
    /// <summary>Ориентир: «у входа в ТЦ», «напротив аптеки».</summary>
    public string? Note { get; set; }

    public bool HasLocation => Lat is not null && Lng is not null;
}

/// <summary>Запись пассажира на маршрут как постоянного (или в очередь).</summary>
public class Membership
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int RouteId { get; set; }
    public Route Route { get; set; } = null!;
    public int StopId { get; set; }
    public Stop Stop { get; set; } = null!;
    public bool Morning { get; set; }
    public bool Evening { get; set; }
    /// <summary>Закреплённое место (одно на утро и вечер). Null — в очереди ожидания.</summary>
    public int? SeatNumber { get; set; }
    public MembershipStatus Status { get; set; }
    public DateOnly From { get; set; }
    public DateOnly? To { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool Covers(Direction d) => d == Direction.Morning ? Morning : Evening;
    public bool IsRegularOn(DateOnly date) =>
        Status == MembershipStatus.Regular && From <= date && (To is null || date <= To);
}

/// <summary>Разовая поездка на свободное место.</summary>
public class OneOffBooking
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int RouteId { get; set; }
    public int StopId { get; set; }
    public Stop Stop { get; set; } = null!;
    public DateOnly Date { get; set; }
    public Direction Direction { get; set; }
    public int? SeatNumber { get; set; }
    public BookingStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Отметка постоянного пассажира «не еду».</summary>
public class Absence
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public DateOnly Date { get; set; }
    public Direction Direction { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class TripCancellation
{
    public int Id { get; set; }
    public int RouteId { get; set; }
    public DateOnly Date { get; set; }
    public Direction Direction { get; set; }
    public string Reason { get; set; } = "";
    public int? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Факт поездки (или неявки) — основа для расчёта.</summary>
public class RideLog
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int RouteId { get; set; }
    public DateOnly Date { get; set; }
    public Direction Direction { get; set; }
    public RideResult Result { get; set; }
    public decimal Price { get; set; }
    public int? MarkedByUserId { get; set; }
    public DateTime MarkedAt { get; set; }
}

public class TripArrival
{
    public int Id { get; set; }
    public int RouteId { get; set; }
    public DateOnly Date { get; set; }
    public Direction Direction { get; set; }
    public DateTime ArrivedAt { get; set; }
}

public class Payment
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public decimal Amount { get; set; }
    public PaymentMethod Method { get; set; }
    /// <summary>Кто принял деньги (обычно водитель маршрута).</summary>
    public string ReceivedBy { get; set; } = "";
    public string? Note { get; set; }
    public DateTime PaidAt { get; set; }
}

public class AuditLog
{
    public int Id { get; set; }
    public string Entity { get; set; } = "";
    public int EntityId { get; set; }
    public string Action { get; set; } = "";
    public string? Details { get; set; }
    public string Actor { get; set; } = "";
    public DateTime At { get; set; }
}
