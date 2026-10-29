using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AzumahCloud.Api.Data;
namespace AzumahCloud.Api.Services;
public static class CloudProjection
{
    public static async Task ApplyAsync(CloudDbContext db, SyncItem item)
    {
        using var doc = JsonDocument.Parse(item.PayloadJson);
        var root = doc.RootElement;
        switch (item.EntityType)
        {
            case "Sale":
                if (!await db.Sales.AnyAsync(x => x.SyncId == item.SyncId))
                    db.Sales.Add(new CloudSale { SyncId = item.SyncId, Id = root.GetProperty("Id").GetInt32(), InvoiceNumber = root.GetProperty("InvoiceNumber").GetString() ?? "", CreatedAt = Utc(root.GetProperty("CreatedAt")), Total = root.GetProperty("Total").GetDecimal(), AmountPaid = root.GetProperty("AmountPaid").GetDecimal(), PaymentMethod = root.GetProperty("PaymentMethod").GetString() ?? "" });
                break;
            case "Payment":
                if (!await db.Payments.AnyAsync(x => x.SyncId == item.SyncId))
                    db.Payments.Add(new CloudPayment { SyncId = item.SyncId, Id = root.GetProperty("Id").GetInt32(), PartyType = root.GetProperty("PartyType").GetInt32(), PartyId = root.GetProperty("PartyId").GetInt32(), Amount = root.GetProperty("Amount").GetDecimal(), BalanceAfter = root.GetProperty("BalanceAfter").GetDecimal(), CreatedAt = Utc(root.GetProperty("CreatedAt")) });
                break;
            case "Expense":
                if (!await db.Expenses.AnyAsync(x => x.SyncId == item.SyncId))
                    db.Expenses.Add(new CloudExpense { SyncId = item.SyncId, Id = root.GetProperty("Id").GetInt32(), Amount = root.GetProperty("Amount").GetDecimal(), Category = root.GetProperty("Category").GetInt32(), Note = root.TryGetProperty("Note", out var note) ? note.GetString() : null, CreatedAt = Utc(root.GetProperty("CreatedAt")) });
                break;            case "Return":
                if (!await db.Returns.AnyAsync(x => x.SyncId == item.SyncId))
                {
                    db.Returns.Add(new CloudReturn
                    {
                        SyncId = item.SyncId,
                        Id = root.GetProperty("Id").GetInt32(),
                        SaleId = root.GetProperty("SaleId").GetInt32(),
                        SaleItemId = root.GetProperty("SaleItemId").GetInt32(),
                        ProductId = root.GetProperty("ProductId").GetInt32(),
                        Quantity = root.GetProperty("Quantity").GetDecimal(),
                        UnitPrice = root.GetProperty("UnitPrice").GetDecimal(),
                        Amount = root.GetProperty("Amount").GetDecimal(),
                        Reason = root.GetProperty("Reason").ToString(),
                        Disposition = root.GetProperty("Disposition").ToString(),
                        RefundMethod = root.GetProperty("RefundMethod").GetString() ?? "",
                        Note = root.TryGetProperty("Note", out var returnNote) ? returnNote.GetString() : null,
                        UserId = root.GetProperty("UserId").GetInt32(),
                        CreatedAt = Utc(root.GetProperty("CreatedAt"))
                    });
                }
                break;
        }
    }
    private static DateTime Utc(JsonElement element)
    {
        DateTime value = element.GetDateTime();
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    public static async Task UpsertSalesAsync(CloudDbContext db, List<SaleSnapshot>? sales)
    {
        if (sales is null || sales.Count == 0) return;
        var syncIds = sales.Where(x => !string.IsNullOrWhiteSpace(x.SyncId)).Select(x => x.SyncId).ToList();
        var existing = await db.Sales.Where(x => syncIds.Contains(x.SyncId)).ToDictionaryAsync(x => x.SyncId);
        foreach (var snap in sales)
        {
            if (string.IsNullOrWhiteSpace(snap.SyncId) || snap.SyncId.Length > 100) continue;
            bool voided = string.Equals(snap.State, "Voided", StringComparison.OrdinalIgnoreCase);
            if (existing.TryGetValue(snap.SyncId, out var row))
            {
                if (voided) { db.Sales.Remove(row); continue; }
                row.Total = snap.Total; row.AmountPaid = snap.AmountPaid; row.ReturnedAmount = snap.ReturnedAmount;
            }
            else if (!voided)
            {
                db.Sales.Add(new CloudSale
                {
                    SyncId = snap.SyncId, Id = snap.Id, InvoiceNumber = CleanText(snap.InvoiceNumber, 60),
                    CreatedAt = snap.CreatedAt.Kind == DateTimeKind.Local ? snap.CreatedAt.ToUniversalTime() : DateTime.SpecifyKind(snap.CreatedAt, DateTimeKind.Utc),
                    Total = snap.Total, AmountPaid = snap.AmountPaid, ReturnedAmount = snap.ReturnedAmount,
                    PaymentMethod = CleanText(snap.PaymentMethod, 30)
                });
            }
        }
    }

    public static string CleanText(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length > max ? cleaned[..max].Trim() : cleaned;
    }

    public static async Task UpsertBusinessProfileAsync(CloudDbContext db, BusinessProfileSnapshot? snapshot)
    {
        string name = CleanText(snapshot?.Name, 80);
        if (snapshot is null || name.Length == 0) return;
        var profile = await db.BusinessProfiles.SingleOrDefaultAsync(x => x.Id == 1);
        if (profile is null) { profile = new CloudBusinessProfile { Id = 1 }; db.BusinessProfiles.Add(profile); }
        profile.Name = name;
        profile.Tagline = CleanText(snapshot.Tagline, 160);
        profile.Phone = CleanText(snapshot.Phone, 40);
        profile.Address = CleanText(snapshot.Address, 200);
        profile.CurrencySymbol = CleanText(snapshot.CurrencySymbol, 8);
    }

    public static async Task ReplaceMasterDataAsync(CloudDbContext db, BootstrapRequest request)
    {
        await UpsertBusinessProfileAsync(db, request.Business);
        await db.Products.ExecuteDeleteAsync(); await db.Customers.ExecuteDeleteAsync(); await db.Suppliers.ExecuteDeleteAsync();
        await UpsertSalesAsync(db, request.Sales);
        foreach (var p in request.Products) db.Products.Add(new CloudProduct { Id = p.Id, Name = p.Name, StockQuantity = p.StockQuantity, ReorderLevel = p.ReorderLevel, Unit = p.Unit });
        foreach (var c in request.Customers) db.Customers.Add(new CloudCustomer { Id = c.Id, Name = c.Name, Phone = c.Phone, Balance = c.Balance });
        foreach (var s in request.Suppliers) db.Suppliers.Add(new CloudSupplier { Id = s.Id, Name = s.Name, Phone = s.Phone, Balance = s.Balance });
    }
}
