namespace GoHome.Web.Domain;

public enum CellKind { Empty, Driver, Seat }

/// <param name="Number">Номер места (только для Seat). Номер закреплён за местом и не сдвигается при правке схемы.</param>
public record SeatCell(int Row, int Col, CellKind Kind, int Number = 0);

/// <summary>
/// Схема салона: прямоугольная сетка. Строка хранения: ряды через «/», ячейки через «,»;
/// ячейка — «D» (водитель), «.» (пусто) или номер места. Пример седана: «D,.,1/2,3,4».
/// Номера мест стабильны: пассажир, севший на место 3, остаётся на месте 3, даже если в схему добавили другие сиденья.
/// </summary>
public sealed class SeatLayout
{
    public const int MaxRows = 8, MaxCols = 5, MaxSeats = 20, MaxNumber = 99;

    private readonly SeatCell[][] _rows;

    private SeatLayout(SeatCell[][] rows) => _rows = rows;

    public IReadOnlyList<IReadOnlyList<SeatCell>> Rows => _rows;
    public int Cols => _rows[0].Length;
    public IEnumerable<SeatCell> Seats => _rows.SelectMany(r => r).Where(c => c.Kind == CellKind.Seat);
    public IReadOnlyList<int> SeatNumbers => Seats.Select(c => c.Number).OrderBy(n => n).ToList();
    public int SeatCount => Seats.Count();
    public bool HasSeat(int number) => Seats.Any(c => c.Number == number);

    /// <summary>Типовая схема: водитель и переднее пассажирское (место 1), дальше ряды по три.</summary>
    public static SeatLayout Default(int seats)
    {
        seats = Math.Clamp(seats, 1, MaxSeats);
        var rows = new List<string[]> { new[] { "D", ".", "1" } };
        var next = 2;
        while (next <= seats)
        {
            var row = new string[3];
            for (var i = 0; i < 3; i++) row[i] = next <= seats ? (next++).ToString() : ".";
            rows.Add(row);
        }
        return TryParse(Serialize(rows), out var layout, out _) ? layout! : throw new InvalidOperationException();
    }

    public static bool TryParse(string? text, out SeatLayout? layout, out string error)
    {
        layout = null;
        error = "";
        if (string.IsNullOrWhiteSpace(text)) { error = "Схема пуста."; return false; }

        var rowsText = text.Split('/', StringSplitOptions.TrimEntries);
        if (rowsText.Length is < 1 or > MaxRows) { error = $"Рядов должно быть от 1 до {MaxRows}."; return false; }

        var rows = new List<SeatCell[]>();
        var numbers = new HashSet<int>();
        var drivers = 0;
        for (var r = 0; r < rowsText.Length; r++)
        {
            var cells = rowsText[r].Split(',', StringSplitOptions.TrimEntries);
            if (cells.Length is < 1 or > MaxCols) { error = $"Мест в ряду должно быть от 1 до {MaxCols}."; return false; }
            if (r > 0 && cells.Length != rows[0].Length) { error = "Все ряды должны быть одной ширины."; return false; }

            var row = new SeatCell[cells.Length];
            for (var c = 0; c < cells.Length; c++)
            {
                var cell = cells[c];
                if (cell == "." || cell.Length == 0) row[c] = new SeatCell(r, c, CellKind.Empty);
                else if (cell.Equals("D", StringComparison.OrdinalIgnoreCase)) { row[c] = new SeatCell(r, c, CellKind.Driver); drivers++; }
                else if (int.TryParse(cell, out var n) && n is >= 1 and <= MaxNumber)
                {
                    if (!numbers.Add(n)) { error = $"Номер места {n} повторяется."; return false; }
                    row[c] = new SeatCell(r, c, CellKind.Seat, n);
                }
                else { error = $"Непонятная ячейка «{cell}»."; return false; }
            }
            rows.Add(row);
        }

        if (drivers > 1) { error = "Водитель может быть только один."; return false; }
        if (numbers.Count == 0) { error = "В схеме нет ни одного места."; return false; }
        if (numbers.Count > MaxSeats) { error = $"Мест не больше {MaxSeats}."; return false; }

        layout = new SeatLayout(rows.ToArray());
        return true;
    }

    public override string ToString() =>
        string.Join('/', _rows.Select(r => string.Join(',', r.Select(c => c.Kind switch
        {
            CellKind.Driver => "D",
            CellKind.Seat => c.Number.ToString(),
            _ => ".",
        }))));

    private static string Serialize(IEnumerable<string[]> rows) => string.Join('/', rows.Select(r => string.Join(',', r)));
}
