using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using AzumahCloud.Api.Data;
using AzumahCloud.Api.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default must be configured.");
var usePostgres = connectionString.StartsWith("Host=", StringComparison.OrdinalIgnoreCase)
    || connectionString.StartsWith("Server=", StringComparison.OrdinalIgnoreCase);
builder.Services.AddDbContext<CloudDbContext>(options =>
{
    if (usePostgres) options.UseNpgsql(connectionString);
    else options.UseSqlite(connectionString);
});
if (builder.Configuration.GetValue("ForwardedHeaders:Enabled", false))
{
    // Behind a trusted platform proxy (Railway): use the real client IP for rate limiting and the real scheme for URLs.
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownNetworks.Clear();
        o.KnownProxies.Clear();
        o.ForwardLimit = 1;
    });
}
var lanEnabled = builder.Configuration.GetValue("LanAccess:Enabled", true);

builder.Services.AddCors(options => options.AddPolicy("ConfiguredOrigins", policy =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
    if (origins.Length > 0) policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
}));
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<LanPosStore>();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = 5,
                QueueLimit = 0
            }));
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = 120,
                QueueLimit = 0
            }));
});

string jwtKey = builder.Configuration["Jwt:SigningKey"] ?? "";
if (jwtKey.Length < 32 || jwtKey.StartsWith("CHANGE_", StringComparison.OrdinalIgnoreCase))
{
    // Never allow a known placeholder secret to sign production tokens.
    if (builder.Environment.IsProduction())
        throw new InvalidOperationException("Set Jwt:SigningKey to a private random secret of at least 32 characters before production startup.");
    // Local LAN-only development can start without a cloud secret; tokens expire
    // on restart because this key is intentionally generated per process.
    jwtKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });
builder.Services.AddAuthorization(options => options.AddPolicy("Owner", policy => policy.RequireRole("Owner")));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
    db.Database.EnsureCreated();
    db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"BusinessProfiles\" (\"Id\" INTEGER NOT NULL PRIMARY KEY, \"Name\" TEXT NOT NULL, \"Tagline\" TEXT NULL, \"Phone\" TEXT NULL, \"Address\" TEXT NULL, \"CurrencySymbol\" TEXT NULL)");
    var passwordService = scope.ServiceProvider.GetRequiredService<PasswordService>();
    var username = builder.Configuration["Owner:Username"] ?? "owner";
    var initialPassword = builder.Configuration["Owner:InitialPassword"] ?? "";
    if (!db.Users.Any() && initialPassword.Length >= 8 && !initialPassword.StartsWith("CHANGE_", StringComparison.OrdinalIgnoreCase))
    {
        db.Users.Add(new CloudUser { Username = username, PasswordHash = passwordService.Hash(initialPassword), Role = "Owner", IsActive = true });
        db.SaveChanges();
    }

    // Only LAN mode requires direct access to the Main PC POS database.
    if (lanEnabled)
    {
        var lanCode = (builder.Configuration["LanAccess:AccessCode"] ?? "").Trim();
        if (lanCode.Length < 12 || lanCode.Equals("AZUMAH-LAN-2026", StringComparison.Ordinal))
            throw new InvalidOperationException("LanAccess:AccessCode must be a private value of at least 12 characters.");
        scope.ServiceProvider.GetRequiredService<LanPosStore>().EnsureReady();
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (app.Configuration.GetValue("ForwardedHeaders:Enabled", false)) app.UseForwardedHeaders();

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Cache-Control"] = "no-store";
    await next();
});
app.UseRateLimiter();
app.UseCors("ConfiguredOrigins");
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", (IWebHostEnvironment env) => Results.Ok(new { ok = true, service = "AzumahCloud.Api", dashboardFiles = env.WebRootPath is not null && File.Exists(Path.Combine(env.WebRootPath, "index.html")) }));

app.MapGet("/readyz", async (CloudDbContext db) =>
{
    try
    {
        return await db.Database.CanConnectAsync()
            ? Results.Ok(new { ok = true, database = "connected" })
            : Results.Json(new { ok = false, error = "Database unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch
    {
        return Results.Json(new { ok = false, error = "Database unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/branding", async (CloudDbContext db) =>
{
    var profile = await db.BusinessProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1);
    return Results.Ok(new { name = string.IsNullOrWhiteSpace(profile?.Name) ? "Owner Dashboard" : profile!.Name, tagline = profile?.Tagline ?? "" });
});

app.MapGet("/api/manifest", async (HttpContext http, CloudDbContext db, IConfiguration config, string? origin) =>
{
    var profile = await db.BusinessProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1);
    string name = string.IsNullOrWhiteSpace(profile?.Name) ? "Owner Dashboard" : profile!.Name;
    string shortName = name.Length <= 12 ? name : name[..12].Trim();
    string self = $"{http.Request.Scheme}://{http.Request.Host}";
    var allowed = (config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>())
        .Select(x => x.TrimEnd('/')).Append(self).ToHashSet(StringComparer.OrdinalIgnoreCase);
    string baseUrl = !string.IsNullOrWhiteSpace(origin) && allowed.Contains(origin.TrimEnd('/')) ? origin.TrimEnd('/') : self;
    var manifest = new
    {
        id = baseUrl + "/",
        name = name + " Owner",
        short_name = shortName,
        description = "Private, read-only business monitoring for " + name + ".",
        start_url = baseUrl + "/",
        scope = baseUrl + "/",
        display = "standalone",
        orientation = "portrait-primary",
        background_color = "#f5f8f5",
        theme_color = "#12372a",
        icons = new object[]
        {
            new { src = baseUrl + "/icons/icon-192.png", sizes = "192x192", type = "image/png", purpose = "any maskable" },
            new { src = baseUrl + "/icons/icon-512.png", sizes = "512x512", type = "image/png", purpose = "any maskable" },
            new { src = baseUrl + "/icons/icon.svg", sizes = "any", type = "image/svg+xml", purpose = "any" }
        }
    };
    return Results.Json(manifest, contentType: "application/manifest+json");
});

app.MapPost("/api/auth/login", async (LoginRequest request, CloudDbContext db, PasswordService passwords, IConfiguration config) =>
{
    var user = await db.Users.SingleOrDefaultAsync(x => x.Username == request.Username && x.IsActive);
    if (user is null || !passwords.Verify(request.Password, user.PasswordHash))
        return Results.Unauthorized();

    var claims = new[]
    {
        new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(ClaimTypes.Role, user.Role)
    };
    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
    var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    var token = new JwtSecurityToken(
        issuer: config["Jwt:Issuer"], audience: config["Jwt:Audience"],
        claims: claims, expires: DateTime.UtcNow.AddHours(8), signingCredentials: creds);
    return Results.Ok(new { accessToken = new JwtSecurityTokenHandler().WriteToken(token), expiresAt = token.ValidTo });
}).RequireRateLimiting("login");

app.MapGet("/api/dashboard", async (CloudDbContext db) =>
{
    var today = DateTime.UtcNow.Date;
    var tomorrow = today.AddDays(1);
    var salesToday = await db.Sales.Where(x => x.CreatedAt >= today && x.CreatedAt < tomorrow).ToListAsync();
    var recent = await db.Sales.OrderByDescending(x => x.CreatedAt).Take(10).Select(x => new
    {
        x.InvoiceNumber, x.CreatedAt, x.Total, x.AmountPaid,
        Balance = x.Total - x.AmountPaid,
        x.PaymentMethod
    }).ToListAsync();
    var low = (await db.Products.Where(x => x.StockQuantity <= x.ReorderLevel).ToListAsync()).OrderBy(x => x.StockQuantity).Take(20).ToList();
    var customerDebt = (await db.Customers.Where(x => x.Balance > 0).ToListAsync()).OrderByDescending(x => x.Balance).Take(20).ToList();
    var supplierDebt = (await db.Suppliers.Where(x => x.Balance > 0).ToListAsync()).OrderByDescending(x => x.Balance).Take(20).ToList();
    var expenseToday = (await db.Expenses.Where(x => x.CreatedAt >= today && x.CreatedAt < tomorrow).ToListAsync()).Sum(x => x.Amount);

    return Results.Ok(new
    {
        date = today,
        salesToday = salesToday.Sum(x => x.Total),
        cashToday = salesToday.Where(x => x.PaymentMethod == "Cash").Sum(x => x.AmountPaid),
        mobileMoneyToday = salesToday.Where(x => x.PaymentMethod == "MobileMoney").Sum(x => x.AmountPaid),
        creditToday = salesToday.Sum(x => Math.Max(0, x.Total - x.AmountPaid)),
        transactionCount = salesToday.Count,
        customerDebt = customerDebt.Sum(x => x.Balance),
        supplierDebt = supplierDebt.Sum(x => x.Balance),
        expenseToday,
        lowStock = low.Count,
        outOfStock = low.Count(x => x.StockQuantity <= 0),
        recent,
        lowStockItems = low.Select(x => new { x.Name, x.StockQuantity, x.ReorderLevel, x.Unit }),
        customersOwing = customerDebt.Select(x => new { x.Name, x.Phone, x.Balance }),
        suppliersOwing = supplierDebt.Select(x => new { x.Name, x.Phone, x.Balance })
    });
}).RequireAuthorization();

app.MapGet("/api/sales", async (CloudDbContext db, int take = 50) =>
    Results.Ok(await db.Sales.OrderByDescending(x => x.CreatedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync()))
    .RequireAuthorization();

app.MapGet("/api/returns", async (CloudDbContext db, int take = 50) =>
    Results.Ok(await db.Returns.OrderByDescending(x => x.CreatedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync()))
    .RequireAuthorization();

app.MapGet("/api/inventory", async (CloudDbContext db) =>
    Results.Ok(await db.Products.OrderBy(x => x.Name).ToListAsync()))
    .RequireAuthorization();

app.MapGet("/api/customers/debts", async (CloudDbContext db) =>
    Results.Ok((await db.Customers.Where(x => x.Balance > 0).ToListAsync()).OrderByDescending(x => x.Balance).ToList()))
    .RequireAuthorization();

app.MapGet("/api/suppliers/debts", async (CloudDbContext db) =>
    Results.Ok((await db.Suppliers.Where(x => x.Balance > 0).ToListAsync()).OrderByDescending(x => x.Balance).ToList()))
    .RequireAuthorization();

app.MapGet("/api/expenses", async (CloudDbContext db, int take = 50) =>
    Results.Ok(await db.Expenses.OrderByDescending(x => x.CreatedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync()))
    .RequireAuthorization();

app.MapPost("/api/sync/push", async (SyncPushRequest request, CloudDbContext db) =>
{
    if (request?.Items is null || request.Items.Count == 0)
        return Results.BadRequest(new { error = "At least one sync item is required." });
    if (request.Items.Count > 500)
        return Results.BadRequest(new { error = "A sync batch cannot exceed 500 items." });

    var allowedTypes = new HashSet<string>(StringComparer.Ordinal) { "Sale", "Payment", "Expense", "Return" };
    foreach (var item in request.Items)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.SyncId) || item.SyncId.Length > 100
            || !allowedTypes.Contains(item.EntityType ?? "")
            || string.IsNullOrWhiteSpace(item.PayloadJson) || item.PayloadJson.Length > 1_000_000)
            return Results.BadRequest(new { error = "A sync item is missing required fields or exceeds allowed limits." });
    }

    await using var transaction = await db.Database.BeginTransactionAsync();
    int accepted = 0;
    foreach (var item in request.Items.DistinctBy(x => x.SyncId))
    {
        if (await db.SyncRecords.AnyAsync(x => x.SyncId == item.SyncId))
            continue;

        db.SyncRecords.Add(new SyncRecord { SyncId = item.SyncId, EntityType = item.EntityType, EntityId = item.EntityId, PayloadJson = item.PayloadJson, ReceivedAt = DateTime.UtcNow });
        await CloudProjection.ApplyAsync(db, item);
        accepted++;
    }
    await db.SaveChangesAsync();
    await transaction.CommitAsync();
    return Results.Ok(new { accepted });
}).RequireAuthorization("Owner");

app.MapPost("/api/sync/bootstrap", async (BootstrapRequest request, CloudDbContext db) =>
{
    if (request?.Products is null || request.Customers is null || request.Suppliers is null)
        return Results.BadRequest(new { error = "Products, customers and suppliers are required." });
    if (request.Products.Count > 100_000 || request.Customers.Count > 100_000 || request.Suppliers.Count > 100_000)
        return Results.BadRequest(new { error = "Bootstrap dataset exceeds the allowed size." });

    await using var transaction = await db.Database.BeginTransactionAsync();
    await CloudProjection.ReplaceMasterDataAsync(db, request);
    await db.SaveChangesAsync();
    await transaction.CommitAsync();
    return Results.Ok(new { ok = true });
}).RequireAuthorization("Owner");


if (lanEnabled)
{
    // -------------------------------------------------------------------------
    // Offline two-PC LAN inventory API
// -----------------------------------------------------------------------------
// PC-2 does not need a cloud account. It authenticates with the LAN access code
// configured on the Main PC. The session token is in-memory and expires on restart.
ConcurrentDictionary<string, DateTime> lanSessions = new(StringComparer.Ordinal);
string lanAccessCode = (builder.Configuration["LanAccess:AccessCode"] ?? "").Trim();
if (lanAccessCode.Length < 8 || lanAccessCode.Equals("AZUMAH-LAN-2026", StringComparison.Ordinal))
    throw new InvalidOperationException("LanAccess:AccessCode must be set to a private value of at least 8 characters.");
ConcurrentDictionary<string, (int Fails, DateTime LockedUntil)> lanFailures = new(StringComparer.Ordinal);

app.MapGet("/api/lan/health", (LanPosStore store) =>
{
    store.EnsureReady();
    return Results.Ok(new { ok = true, mode = "offline-lan", database = "Main POS database" });
});

app.MapGet("/api/lan/branding", (LanPosStore store) => Results.Ok(new { name = store.GetBusinessName() }));

app.MapPost("/api/lan/login", (HttpContext http, LanLoginRequest request) =>
{
    string ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (lanFailures.TryGetValue(ip, out var state) && state.LockedUntil > DateTime.UtcNow)
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);

    byte[] supplied = Encoding.UTF8.GetBytes(request.AccessCode?.Trim() ?? "");
    byte[] expected = Encoding.UTF8.GetBytes(lanAccessCode);
    bool valid = supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    if (!valid)
    {
        lanFailures.AddOrUpdate(ip,
            _ => (1, DateTime.MinValue),
            (_, cur) => cur.Fails + 1 >= 5 ? (0, DateTime.UtcNow.AddMinutes(10)) : (cur.Fails + 1, DateTime.MinValue));
        return Results.Unauthorized();
    }
    lanFailures.TryRemove(ip, out _);

    foreach (var kv in lanSessions)
        if (kv.Value <= DateTime.UtcNow) lanSessions.TryRemove(kv.Key, out _);

    string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    lanSessions[token] = DateTime.UtcNow.AddHours(12);
    return Results.Ok(new { accessToken = token, expiresAt = lanSessions[token] });
}).RequireRateLimiting("login");

app.MapGet("/api/lan/products", (HttpContext http, LanPosStore store, string? search) =>
{
    if (!LanAuthorized(http, lanSessions)) return Results.Unauthorized();
    return Results.Ok(store.GetProducts(search));
});

app.MapPost("/api/lan/products", (HttpContext http, LanPosStore store, LanProductRequest request) =>
{
    if (!LanAuthorized(http, lanSessions)) return Results.Unauthorized();
    try { return Results.Ok(store.AddProduct(request)); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPut("/api/lan/products/{id:int}", (HttpContext http, LanPosStore store, int id, LanProductRequest request) =>
{
    if (!LanAuthorized(http, lanSessions)) return Results.Unauthorized();
    try { return Results.Ok(store.UpdateProduct(id, request)); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/lan/restock", (HttpContext http, LanPosStore store, RestockRequest request) =>
{
    if (!LanAuthorized(http, lanSessions)) return Results.Unauthorized();
    try { return Results.Ok(store.Restock(request)); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/lan/restock/xlsx", async (HttpContext http, LanPosStore store, IFormFile file) =>
{
    if (!LanAuthorized(http, lanSessions)) return Results.Unauthorized();
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Select an Excel .xlsx file." });
    if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Only Excel .xlsx files are supported." });
    if (file.Length > 10 * 1024 * 1024)
        return Results.BadRequest(new { error = "Excel file is too large. Maximum size is 10 MB." });

    try
    {
        List<Dictionary<string, string>> rows;
        await using (Stream input = file.OpenReadStream())
            rows = XlsxRestockReader.Read(input);

        List<object> results = new();
        int added = 0, restocked = 0, failed = 0;

        foreach (Dictionary<string, string> row in rows)
        {
            try
            {
                string code = Get(row, "productcode", "barcode", "code", "productid", "sku");
                string name = Get(row, "productname", "name", "product");
                string quantityText = Get(row, "quantity", "qty", "restockquantity", "stock");
                if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Product code/barcode is missing.");
                if (!decimal.TryParse(quantityText, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal qty) || qty <= 0)
                    throw new InvalidOperationException("Quantity must be a number greater than zero.");

                bool existed = store.GetProducts(code).Any(p => string.Equals(p.Barcode, code.Trim(), StringComparison.OrdinalIgnoreCase));
                decimal cost = ParseDecimal(Get(row, "costprice", "cost", "buyingprice"));
                decimal price = ParseDecimal(Get(row, "sellingprice", "price", "saleprice"));
                decimal reorder = ParseDecimal(Get(row, "reorderlevel", "reorder", "minimumstock"));
                RestockRequest request = new(
                    code.Trim(), qty, EmptyToNull(name), EmptyToNull(Get(row, "brand")),
                    EmptyToNull(Get(row, "unit")), EmptyToNull(Get(row, "category")),
                    cost, price, reorder);

                LanProduct product = store.Restock(request);
                if (existed) restocked++; else added++;
                results.Add(new { row = results.Count + 2, ok = true, code = code.Trim(), action = existed ? "RESTOCKED" : "ADDED", stock = product.StockQuantity });
            }
            catch (Exception ex)
            {
                failed++;
                results.Add(new { row = results.Count + 2, ok = false, error = ex.Message });
            }
        }

        return Results.Ok(new { totalRows = rows.Count, added, restocked, failed, results });
    }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (Exception ex) { return Results.BadRequest(new { error = $"Excel import failed: {ex.Message}" }); }
});

app.MapGet("/api/lan/restock/template", (HttpContext http) =>
{
    if (!LanAuthorized(http, lanSessions)) return Results.Unauthorized();
    const string csv = "Product Code,Product Name,Quantity,Brand,Unit,Category,Cost Price,Selling Price,Reorder Level\r\n" +
                       "PVC-001,PVC Pipe 1/2,10,Generic,Piece,General,20,38,5\r\n";
    return Results.Text(csv, "text/csv");
});

}

static bool LanAuthorized(HttpContext http, ConcurrentDictionary<string, DateTime> sessions)
{
    string? value = http.Request.Headers.Authorization.FirstOrDefault();
    if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return false;
    string token = value["Bearer ".Length..].Trim();
    if (!sessions.TryGetValue(token, out DateTime expiry)) return false;
    if (expiry <= DateTime.UtcNow)
    {
        sessions.TryRemove(token, out _);
        return false;
    }
    return true;
}

static string Get(Dictionary<string, string> row, params string[] keys)
{
    foreach (string key in keys)
    {
        string normalized = new string(key.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (row.TryGetValue(normalized, out string? value) && !string.IsNullOrWhiteSpace(value))
            return value.Trim();
    }
    return "";
}

static decimal ParseDecimal(string value)
{
    if (string.IsNullOrWhiteSpace(value)) return 0m;
    if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal result)) return result;
    if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out result)) return result;
    throw new InvalidOperationException($"Invalid number '{value}'.");
}

static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

app.MapFallbackToFile("index.html");
app.Run();

public record LanLoginRequest(string AccessCode);
record LoginRequest(string Username, string Password);
record SyncPushRequest(List<SyncItem> Items);
public record SyncItem(string SyncId, string EntityType, int EntityId, string PayloadJson);
public record BusinessProfileSnapshot(string Name, string? Tagline, string? Phone, string? Address, string? CurrencySymbol);
public record BootstrapRequest(List<ProductSnapshot> Products, List<CustomerSnapshot> Customers, List<SupplierSnapshot> Suppliers, BusinessProfileSnapshot? Business = null);
public record ProductSnapshot(int Id, string Name, decimal StockQuantity, decimal ReorderLevel, string Unit);
public record CustomerSnapshot(int Id, string Name, string? Phone, decimal Balance);
public record SupplierSnapshot(int Id, string Name, string? Phone, decimal Balance);