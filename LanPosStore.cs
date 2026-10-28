using Microsoft.Data.Sqlite;
using System.Globalization;

namespace AzumahCloud.Api.Services;

/// <summary>
/// Direct LAN access to the POS database on the Main PC.
/// This is intentionally separate from the existing cloud projection database.
/// All writes use SQLite transactions and atomic stock increments so sales and
/// restocks can safely occur at the same time.
/// </summary>
public sealed class LanPosStore
{
    private readonly string _dbPath;
    private readonly ILogger<LanPosStore> _logger;

    public LanPosStore(IConfiguration configuration, ILogger<LanPosStore> logger)
    {
        _logger = logger;
        string configured = configuration["LanDatabase:Path"] ?? "";
        _dbPath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzumahEnterprise", "azumah.db")
            : Environment.ExpandEnvironmentVariables(configured);

        string? folder = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);
    }

    public string DatabasePath => _dbPath;

    public void EnsureReady()
    {
        if (!File.Exists(_dbPath))
            throw new FileNotFoundException($"Main POS database was not found: {_dbPath}");

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=15000;";
        command.ExecuteNonQuery();
    }

    public List<LanProduct> GetProducts(string? search = null)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT p.Id, p.Name, p.Barcode, COALESCE(p.Brand,''), p.Unit,
                   p.CategoryId, COALESCE(c.Name,'General'), p.CostPrice,
                   p.SellingPrice, p.StockQuantity, p.ReorderLevel, p.IsActive
            FROM Products p
            LEFT JOIN Categories c ON c.Id = p.CategoryId
            WHERE p.IsActive = 1
              AND ($search = '' OR p.Name LIKE $like OR p.Barcode LIKE $like OR COALESCE(p.Brand,'') LIKE $like)
            ORDER BY p.Name COLLATE NOCASE;
            """;
        string value = search?.Trim() ?? "";
        cmd.Parameters.AddWithValue("$search", value);
        cmd.Parameters.AddWithValue("$like", $"%{value}%");

        using SqliteDataReader reader = cmd.ExecuteReader();
        List<LanProduct> result = new();
        while (reader.Read()) result.Add(ReadProduct(reader));
        return result;
    }

    public LanProduct? GetProduct(int id)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT p.Id, p.Name, p.Barcode, COALESCE(p.Brand,''), p.Unit,
                   p.CategoryId, COALESCE(c.Name,'General'), p.CostPrice,
                   p.SellingPrice, p.StockQuantity, p.ReorderLevel, p.IsActive
            FROM Products p
            LEFT JOIN Categories c ON c.Id = p.CategoryId
            WHERE p.Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? ReadProduct(reader) : null;
    }

    public LanProduct AddProduct(LanProductRequest request)
    {
        ValidateProduct(request, allowZeroStock: true);
        using SqliteConnection db = Open();
        using SqliteTransaction tx = db.BeginTransaction();

        int categoryId = ResolveCategoryId(db, tx, request.Category);
        if (FindIdByBarcode(db, tx, request.Barcode) is int existing)
            throw new InvalidOperationException($"Product code/barcode '{request.Barcode}' already exists (ID {existing}).");

        using (SqliteCommand cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO Products
                    (Name, Barcode, Brand, Unit, CategoryId, CostPrice, SellingPrice,
                     StockQuantity, ReorderLevel, IsActive)
                VALUES
                    ($name, $barcode, $brand, $unit, $categoryId, $cost, $price,
                     $stock, $reorder, 1);
                SELECT last_insert_rowid();
                """;
            AddProductParameters(cmd, request, categoryId);
            int id = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);

            if (request.StockQuantity > 0)
                AddMovement(db, tx, id, request.StockQuantity, "LAN product creation / opening stock");
            tx.Commit();
            return GetProduct(id) ?? throw new InvalidOperationException("Product was created but could not be reloaded.");
        }
    }

    public LanProduct UpdateProduct(int id, LanProductRequest request)
    {
        ValidateProduct(request, allowZeroStock: true);
        using SqliteConnection db = Open();
        using SqliteTransaction tx = db.BeginTransaction();

        if (FindIdByBarcode(db, tx, request.Barcode) is int other && other != id)
            throw new InvalidOperationException($"Product code/barcode '{request.Barcode}' already belongs to another product.");

        int categoryId = ResolveCategoryId(db, tx, request.Category);
        using (SqliteCommand cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE Products
                   SET Name=$name, Barcode=$barcode, Brand=$brand, Unit=$unit,
                       CategoryId=$categoryId, CostPrice=$cost, SellingPrice=$price,
                       ReorderLevel=$reorder
                 WHERE Id=$id AND IsActive=1;
                """;
            AddProductParameters(cmd, request, categoryId);
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteNonQuery() == 0)
                throw new KeyNotFoundException("Product not found or inactive.");
        }
        tx.Commit();
        return GetProduct(id) ?? throw new InvalidOperationException("Product was updated but could not be reloaded.");
    }

    public LanProduct Restock(RestockRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Barcode))
            throw new InvalidOperationException("Product code/barcode is required.");
        if (request.Quantity <= 0)
            throw new InvalidOperationException("Restock quantity must be greater than zero.");

        using SqliteConnection db = Open();
        using SqliteTransaction tx = db.BeginTransaction();

        int? id = FindIdByBarcode(db, tx, request.Barcode.Trim());
        if (id is null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                throw new InvalidOperationException($"Product '{request.Barcode}' does not exist. A name is required to create it.");

            LanProductRequest product = new(
                request.Name.Trim(), request.Barcode.Trim(), request.Brand?.Trim() ?? "",
                request.Unit?.Trim() ?? "Piece", request.Category?.Trim() ?? "General",
                request.CostPrice, request.SellingPrice, request.Quantity, request.ReorderLevel);

            int categoryId = ResolveCategoryId(db, tx, product.Category);
            using SqliteCommand insert = db.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO Products
                    (Name, Barcode, Brand, Unit, CategoryId, CostPrice, SellingPrice,
                     StockQuantity, ReorderLevel, IsActive)
                VALUES
                    ($name,$barcode,$brand,$unit,$categoryId,$cost,$price,$stock,$reorder,1);
                SELECT last_insert_rowid();
                """;
            AddProductParameters(insert, product, categoryId);
            id = Convert.ToInt32(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            AddMovement(db, tx, id.Value, request.Quantity, "LAN Excel/manual restock - new product");
        }
        else
        {
            using SqliteCommand update = db.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE Products
                   SET StockQuantity = StockQuantity + $quantity
                 WHERE Id = $id AND IsActive = 1;
                """;
            update.Parameters.AddWithValue("$quantity", request.Quantity);
            update.Parameters.AddWithValue("$id", id.Value);
            if (update.ExecuteNonQuery() == 0)
                throw new KeyNotFoundException($"Product '{request.Barcode}' is no longer active.");
            AddMovement(db, tx, id.Value, request.Quantity, "LAN Excel/manual restock");
        }

        tx.Commit();
        return GetProduct(id.Value) ?? throw new InvalidOperationException("Restock completed but product could not be reloaded.");
    }

    private SqliteConnection Open()
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 15
        };
        SqliteConnection db = new(builder.ConnectionString);
        db.Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=15000; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return db;
    }

    private static int? FindIdByBarcode(SqliteConnection db, SqliteTransaction tx, string barcode)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT Id FROM Products WHERE Barcode=$barcode COLLATE NOCASE LIMIT 1;";
        cmd.Parameters.AddWithValue("$barcode", barcode);
        object? value = cmd.ExecuteScalar();
        return value is null || value == DBNull.Value ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static int ResolveCategoryId(SqliteConnection db, SqliteTransaction tx, string? category)
    {
        string name = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim();
        using SqliteCommand find = db.CreateCommand();
        find.Transaction = tx;
        find.CommandText = "SELECT Id FROM Categories WHERE Name=$name COLLATE NOCASE LIMIT 1;";
        find.Parameters.AddWithValue("$name", name);
        object? found = find.ExecuteScalar();
        if (found is not null && found != DBNull.Value) return Convert.ToInt32(found, CultureInfo.InvariantCulture);

        using SqliteCommand insert = db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO Categories(Name) VALUES($name); SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("$name", name);
        return Convert.ToInt32(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void AddMovement(SqliteConnection db, SqliteTransaction tx, int productId, decimal quantity, string reason)
    {
        using SqliteCommand movement = db.CreateCommand();
        movement.Transaction = tx;
        movement.CommandText = """
            INSERT INTO InventoryMovements(ProductId, Type, QuantityChange, Reason, CreatedAt, UserId)
            VALUES($productId, 0, $quantity, $reason, $createdAt, 0);
            """;
        movement.Parameters.AddWithValue("$productId", productId);
        movement.Parameters.AddWithValue("$quantity", quantity.ToString(CultureInfo.InvariantCulture));
        movement.Parameters.AddWithValue("$reason", reason);
        movement.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        movement.ExecuteNonQuery();
    }

    private static void AddProductParameters(SqliteCommand cmd, LanProductRequest p, int categoryId)
    {
        cmd.Parameters.AddWithValue("$name", p.Name.Trim());
        cmd.Parameters.AddWithValue("$barcode", p.Barcode.Trim());
        cmd.Parameters.AddWithValue("$brand", string.IsNullOrWhiteSpace(p.Brand) ? DBNull.Value : p.Brand.Trim());
        cmd.Parameters.AddWithValue("$unit", ParseUnit(p.Unit));
        cmd.Parameters.AddWithValue("$categoryId", categoryId);
        cmd.Parameters.AddWithValue("$cost", p.CostPrice.ToString(CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$price", p.SellingPrice.ToString(CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$stock", p.StockQuantity.ToString(CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$reorder", p.ReorderLevel.ToString(CultureInfo.InvariantCulture));
    }

    private static int ParseUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit)) return 0;
        string value = unit.Trim();
        string[] names = ["Piece", "Meter", "Roll", "Bag", "Box", "Length", "Kg", "Litre", "Pack"];
        int index = Array.FindIndex(names, x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) return index;
        if (int.TryParse(value, out int numeric) && numeric >= 0 && numeric < names.Length) return numeric;
        throw new InvalidOperationException($"Unknown unit '{unit}'. Use Piece, Meter, Roll, Bag, Box, Length, Kg, Litre or Pack.");
    }

    private static void ValidateProduct(LanProductRequest request, bool allowZeroStock)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidOperationException("Product name is required.");
        if (string.IsNullOrWhiteSpace(request.Barcode)) throw new InvalidOperationException("Product code/barcode is required.");
        if (request.CostPrice < 0 || request.SellingPrice < 0) throw new InvalidOperationException("Prices cannot be negative.");
        if ((!allowZeroStock && request.StockQuantity <= 0) || request.StockQuantity < 0)
            throw new InvalidOperationException("Stock quantity cannot be negative.");
        if (request.ReorderLevel < 0) throw new InvalidOperationException("Reorder level cannot be negative.");
    }

    private static LanProduct ReadProduct(SqliteDataReader r) => new(
        r.GetInt32(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3),
        r.GetInt32(4), r.GetInt32(5), r.GetString(6), r.GetDecimal(7), r.GetDecimal(8),
        r.GetDecimal(9), r.GetDecimal(10), r.GetBoolean(11));
}

public sealed record LanProduct(
    int Id, string Name, string Barcode, string Brand, int Unit, int CategoryId,
    string Category, decimal CostPrice, decimal SellingPrice, decimal StockQuantity,
    decimal ReorderLevel, bool IsActive);

public sealed record LanProductRequest(
    string Name, string Barcode, string? Brand, string? Unit, string? Category,
    decimal CostPrice, decimal SellingPrice, decimal StockQuantity, decimal ReorderLevel);

public sealed record RestockRequest(
    string Barcode, decimal Quantity, string? Name, string? Brand, string? Unit, string? Category,
    decimal CostPrice, decimal SellingPrice, decimal ReorderLevel);
