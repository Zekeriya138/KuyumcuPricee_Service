using System.Globalization;
using KuyumcuDesktop.Models;
using KuyumcuDesktop.Services;

namespace KuyumcuDesktop.Views;

public partial class SalesViewForm : Form
{
    private readonly ApiClient _api;
    private readonly List<SaleLineRow> _rows = new();
    private List<QuoteDto> _rates = new();
    private List<CustomerDto> _customers = new();
    private System.Windows.Forms.Timer? _timerRates;
    private decimal _indirimTotal;
    private Guid _branchId; // Şube seçimi uygulamada yapılacak; şimdilik sabit veya config

    public SalesViewForm(ApiClient api, Guid branchId)
    {
        _api = api;
        _branchId = branchId;
        InitializeComponent();
        _indirimTotal = 0m;
    }

    private async void SalesViewForm_Load(object? sender, EventArgs e)
    {
        txtBarcode.Focus();
        await RefreshRatesAsync();
        _timerRates = new System.Windows.Forms.Timer { Interval = 60_000 }; // 1 dk
        _timerRates.Tick += async (_, _) => await RefreshRatesAsync();
        _timerRates.Start();
    }

    private async Task RefreshRatesAsync()
    {
        try
        {
            _rates = await _api.GetPricesLatestAsync();
            UpdateRatesLabels();
            RecalcAllRows();
        }
        catch
        {
            lblRatesAge.Text = "Kurlar yüklenemedi.";
        }
    }

    private void UpdateRatesLabels()
    {
        var g24 = _rates.FirstOrDefault(x => string.Equals(x.Code, "G24_TRY", StringComparison.OrdinalIgnoreCase));
        var g22 = _rates.FirstOrDefault(x => string.Equals(x.Code, "G22_TRY", StringComparison.OrdinalIgnoreCase));
        var g14 = _rates.FirstOrDefault(x => string.Equals(x.Code, "G14_TRY", StringComparison.OrdinalIgnoreCase));

        lblG24.Text = $"24 Ayar: {(g24 != null ? g24.Ask.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) : "—")} ₺";
        lblG22.Text = $"22 Ayar: {(g22 != null ? g22.Ask.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) : "—")} ₺";
        lblG14.Text = $"14 Ayar: {(g14 != null ? g14.Ask.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) : "—")} ₺";
        lblRatesAge.Text = "Son güncelleme: " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.GetCultureInfo("tr-TR"));
    }

    private decimal GetAyarKuru(string ayar)
    {
        string code = ayar?.ToUpperInvariant()?.Replace(" ", "") switch
        {
            "24K" or "24" => "G24_TRY",
            "22K" or "22" => "G22_TRY",
            "14K" or "14" => "G14_TRY",
            _ => "G22_TRY"
        };
        var q = _rates.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
        return q?.Ask ?? 0m;
    }

    private async void TxtBarcode_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter) return;
        e.SuppressKeyPress = true;
        var code = txtBarcode.Text?.Trim();
        if (string.IsNullOrEmpty(code)) return;

        var item = await _api.GetProductItemByBarcodeAsync(code);
        if (item == null)
        {
            MessageBox.Show("Bu barkoda ait ürün bulunamadı.", "Satış", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!item.IsInStock)
        {
            MessageBox.Show("Ürün stokta değil veya satılmış.", "Satış", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var row = new SaleLineRow
        {
            ProductItemId = item.Id,
            ProductCode = item.ProductCode,
            Barcode = item.Barcode ?? "",
            ProductName = item.ProductName,
            Gram = item.Weight,
            Ayar = string.IsNullOrWhiteSpace(item.Karat) ? "22K" : item.Karat,
            AyarKuru = GetAyarKuru(item.Karat ?? "22K"),
            IsciLik = 0m
        };

        _rows.Add(row);
        AddRowToGrid(row);
        txtBarcode.Clear();
        txtBarcode.Focus();
        UpdateSummary();
    }

    private void AddRowToGrid(SaleLineRow row)
    {
        var idx = grdLines.Rows.Add(
            row.Barcode,
            row.ProductName,
            row.Gram.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")),
            row.Ayar,
            row.IsciLik.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")),
            row.ToplamTutar.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")),
            row.ProductItemId.ToString(),
            row.ProductCode
        );
        grdLines.Rows[idx].Tag = row;
    }

    private void RecalcAllRows()
    {
        foreach (DataGridViewRow r in grdLines.Rows)
        {
            if (r.Tag is not SaleLineRow row) continue;
            row.AyarKuru = GetAyarKuru(row.Ayar);
            r.Cells[colToplam.Index].Value = row.ToplamTutar.ToString("N2", CultureInfo.GetCultureInfo("tr-TR"));
        }
        UpdateSummary();
    }

    private void GrdLines_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.ColumnIndex != colIsciLik.Index) return;
        if (grdLines.Rows[e.RowIndex].Tag is not SaleLineRow row) return;

        var s = grdLines.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString();
        if (decimal.TryParse(s?.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
            row.IsciLik = val;

        grdLines.Rows[e.RowIndex].Cells[colToplam.Index].Value = row.ToplamTutar.ToString("N2", CultureInfo.GetCultureInfo("tr-TR"));
        UpdateSummary();
    }

    private void GrdLines_UserDeletingRow(object? sender, DataGridViewRowCancelEventArgs e)
    {
        if (e.Row?.Tag is SaleLineRow row)
            _rows.Remove(row);
        UpdateSummary();
    }

    private void TxtIndirim_TextChanged(object? sender, EventArgs e)
    {
        var s = txtIndirim.Text?.Replace(",", ".");
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var val) && val >= 0)
            _indirimTotal = val;
        else
            _indirimTotal = 0m;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        decimal genel = 0m;
        foreach (DataGridViewRow r in grdLines.Rows)
            if (r.Tag is SaleLineRow row)
                genel += row.ToplamTutar;

        lblGenelToplam.Text = genel.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";
        lblKdv.Text = "0,00 ₺"; // İsteğe göre KDV hesaplanabilir
        decimal net = Math.Max(0, genel - _indirimTotal);
        lblNetTutar.Text = net.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";
    }

    private async void CboCustomer_TextUpdate(object? sender, EventArgs e)
    {
        var q = cboCustomer.Text?.Trim();
        if (string.IsNullOrEmpty(q) || q.Length < 2)
        {
            cboCustomer.DroppedDown = false;
            return;
        }
        try
        {
            _customers = await _api.GetCustomersAsync(q);
            cboCustomer.Items.Clear();
            foreach (var c in _customers)
                cboCustomer.Items.Add(new CustomerItem(c));
            cboCustomer.DroppedDown = true;
        }
        catch
        {
            cboCustomer.DroppedDown = false;
        }
    }

    private async void BtnComplete_Click(object? sender, EventArgs e)
    {
        if (_rows.Count == 0)
        {
            MessageBox.Show("En az bir ürün ekleyin.", "Satış", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (rdoVeresiye.Checked)
        {
            var sel = cboCustomer.SelectedItem as CustomerItem;
            if (sel == null || string.IsNullOrWhiteSpace(cboCustomer.Text))
            {
                MessageBox.Show("Veresiye için müşteri seçin.", "Satış", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        var req = new CreateSaleReqV2
        {
            BranchId = _branchId,
            CustomerId = rdoVeresiye.Checked && cboCustomer.SelectedItem is CustomerItem ci ? ci.Id : null,
            Items = new List<CreateSaleItemReq>()
        };

        int lineNo = 0;
        foreach (var row in _rows)
        {
            req.Items.Add(new CreateSaleItemReq
            {
                LineNo = ++lineNo,
                ProductCode = row.ProductCode,
                ProductName = row.ProductName,
                Karat = row.Ayar,
                Category = null,
                Quantity = row.Gram,
                UnitPrice = row.Gram > 0 ? (row.ToplamTutar / row.Gram) : 0m,
                Discount = 0m,
                TaxRate = 0m,
                ProductItemId = row.ProductItemId
            });
        }

        try
        {
            var (success, error) = await _api.CreateSaleAsync(req);
            if (success)
            {
                MessageBox.Show("Satış kaydedildi.", "Satış", MessageBoxButtons.OK, MessageBoxIcon.Information);
                BtnClear_Click(sender, e);
            }
            else
                MessageBox.Show(error ?? "Satış kaydedilemedi.", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void BtnClear_Click(object? sender, EventArgs e)
    {
        _rows.Clear();
        grdLines.Rows.Clear();
        txtIndirim.Text = "0";
        _indirimTotal = 0m;
        cboCustomer.SelectedItem = null;
        cboCustomer.Text = "";
        rdoNakit.Checked = true;
        txtBarcode.Focus();
        UpdateSummary();
    }

    private sealed class CustomerItem
    {
        public Guid Id { get; }
        public string Display { get; }
        public CustomerItem(CustomerDto d) { Id = d.Id; Display = d.FullName + (string.IsNullOrEmpty(d.Phone) ? "" : " - " + d.Phone); }
        public override string ToString() => Display;
    }
}
