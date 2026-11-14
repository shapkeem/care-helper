namespace CareHelper.Excel;

/// <summary>
/// 엑셀 한 장. 값은 파이썬(openpyxl/xlrd)과 같은 종류로 둔다:
/// null, string, long(정수), double, DateTime, TimeOnly, TimeSpan, bool.
/// </summary>
public sealed class Sheet
{
    readonly Dictionary<(int Row, int Col), object?> _cells = new();

    public string Title { get; init; } = "";
    public int MaxRow { get; private set; }
    public int MaxColumn { get; private set; }

    /// <summary>1부터 세는 행·열 (openpyxl ws.cell(row, column) 과 같음).</summary>
    public object? Cell(int row, int column) => _cells.TryGetValue((row, column), out var v) ? v : null;

    public void Set(int row, int column, object? value)
    {
        _cells[(row, column)] = value;
        Touch(row, column);
    }

    /// <summary>값이 없어도 칸이 있으면(서식만 있는 칸) 크기에 넣는다 (openpyxl max_row 와 같게).</summary>
    public void Touch(int row, int column)
    {
        if (row > MaxRow) MaxRow = row;
        if (column > MaxColumn) MaxColumn = column;
    }

    /// <summary>openpyxl iter_rows(min_row, max_col, values_only=True) 와 같음.</summary>
    public IEnumerable<object?[]> Rows(int minRow = 1, int? maxCol = null)
    {
        int cols = maxCol ?? MaxColumn;
        for (int r = minRow; r <= MaxRow; r++)
        {
            var row = new object?[cols];
            for (int c = 1; c <= cols; c++)
                row[c - 1] = Cell(r, c);
            yield return row;
        }
    }
}

public static class SheetLoader
{
    /// <summary>제공계획서처럼 첫(활성) 장을 읽는다. .xls 는 xlrd 처럼, 그 밖은 openpyxl(data_only) 처럼.</summary>
    public static Sheet LoadActive(string path)
    {
        if (Path.GetExtension(path).Equals(".xls", StringComparison.OrdinalIgnoreCase))
            return XlsReader.ReadFirst(File.ReadAllBytes(path));
        return XlsxReader.Read(File.ReadAllBytes(path), active: true);
    }
}
