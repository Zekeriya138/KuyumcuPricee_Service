namespace KuyumcuDesktop.Views;

partial class SalesViewForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
            components.Dispose();
        _timerRates?.Dispose();
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        lblBarcode = new Label();
        txtBarcode = new TextBox();
        pnlRates = new Panel();
        lblRatesTitle = new Label();
        lblG24 = new Label();
        lblG22 = new Label();
        lblG14 = new Label();
        lblRatesAge = new Label();
        grdLines = new DataGridView();
        colBarcode = new DataGridViewTextBoxColumn();
        colProductName = new DataGridViewTextBoxColumn();
        colGram = new DataGridViewTextBoxColumn();
        colAyar = new DataGridViewTextBoxColumn();
        colIsciLik = new DataGridViewTextBoxColumn();
        colToplam = new DataGridViewTextBoxColumn();
        colProductItemId = new DataGridViewTextBoxColumn();
        colProductCode = new DataGridViewTextBoxColumn();
        pnlRight = new Panel();
        grpPayment = new GroupBox();
        rdoTakas = new RadioButton();
        rdoVeresiye = new RadioButton();
        rdoKrediKarti = new RadioButton();
        rdoNakit = new RadioButton();
        grpCustomer = new GroupBox();
        cboCustomer = new ComboBox();
        lblCustomer = new Label();
        pnlSummary = new Panel();
        lblNetTutar = new Label();
        lblNetCaption = new Label();
        lblIndirim = new Label();
        lblIndirimCaption = new Label();
        lblKdv = new Label();
        lblKdvCaption = new Label();
        lblGenelToplam = new Label();
        lblGenelCaption = new Label();
        txtIndirim = new TextBox();
        btnComplete = new Button();
        btnClear = new Button();
        pnlRates.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)grdLines).BeginInit();
        pnlRight.SuspendLayout();
        grpPayment.SuspendLayout();
        grpCustomer.SuspendLayout();
        pnlSummary.SuspendLayout();
        SuspendLayout();
        //
        // lblBarcode
        //
        lblBarcode.AutoSize = true;
        lblBarcode.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        lblBarcode.Location = new Point(16, 14);
        lblBarcode.Name = "lblBarcode";
        lblBarcode.Size = new Size(55, 19);
        lblBarcode.TabIndex = 0;
        lblBarcode.Text = "Barkod";
        //
        // txtBarcode
        //
        txtBarcode.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        txtBarcode.Font = new Font("Segoe UI", 12F);
        txtBarcode.Location = new Point(16, 38);
        txtBarcode.Name = "txtBarcode";
        txtBarcode.PlaceholderText = "Barkodu okutun veya yazın...";
        txtBarcode.Size = new Size(420, 29);
        txtBarcode.TabIndex = 1;
        txtBarcode.KeyDown += TxtBarcode_KeyDown;
        //
        // pnlRates
        //
        pnlRates.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        pnlRates.BackColor = Color.FromArgb(30, 41, 59);
        pnlRates.Controls.Add(lblRatesTitle);
        pnlRates.Controls.Add(lblG24);
        pnlRates.Controls.Add(lblG22);
        pnlRates.Controls.Add(lblG14);
        pnlRates.Controls.Add(lblRatesAge);
        pnlRates.ForeColor = Color.White;
        pnlRates.Location = new Point(720, 12);
        pnlRates.Name = "pnlRates";
        pnlRates.Padding = new Padding(10);
        pnlRates.Size = new Size(260, 140);
        pnlRates.TabIndex = 2;
        //
        // lblRatesTitle
        //
        lblRatesTitle.AutoSize = true;
        lblRatesTitle.Font = new Font("Segoe UI", 9.75F, FontStyle.Bold);
        lblRatesTitle.Location = new Point(10, 10);
        lblRatesTitle.Name = "lblRatesTitle";
        lblRatesTitle.Size = new Size(118, 17);
        lblRatesTitle.TabIndex = 0;
        lblRatesTitle.Text = "Anlık Altın Kurları";
        //
        // lblG24
        //
        lblG24.AutoSize = true;
        lblG24.Font = new Font("Segoe UI", 9F);
        lblG24.Location = new Point(10, 36);
        lblG24.Name = "lblG24";
        lblG24.Size = new Size(90, 15);
        lblG24.TabIndex = 1;
        lblG24.Text = "24 Ayar: 0,00 ₺";
        //
        // lblG22
        //
        lblG22.AutoSize = true;
        lblG22.Font = new Font("Segoe UI", 9F);
        lblG22.Location = new Point(10, 56);
        lblG22.Name = "lblG22";
        lblG22.Size = new Size(90, 15);
        lblG22.TabIndex = 2;
        lblG22.Text = "22 Ayar: 0,00 ₺";
        //
        // lblG14
        //
        lblG14.AutoSize = true;
        lblG14.Font = new Font("Segoe UI", 9F);
        lblG14.Location = new Point(10, 76);
        lblG14.Name = "lblG14";
        lblG14.Size = new Size(90, 15);
        lblG14.TabIndex = 3;
        lblG14.Text = "14 Ayar: 0,00 ₺";
        //
        // lblRatesAge
        //
        lblRatesAge.AutoSize = true;
        lblRatesAge.Font = new Font("Segoe UI", 8F);
        lblRatesAge.ForeColor = Color.Silver;
        lblRatesAge.Location = new Point(10, 100);
        lblRatesAge.Name = "lblRatesAge";
        lblRatesAge.Size = new Size(120, 13);
        lblRatesAge.TabIndex = 4;
        lblRatesAge.Text = "Son güncelleme: --";
        //
        // grdLines
        //
        grdLines.AllowUserToAddRows = false;
        grdLines.AllowUserToDeleteRows = true;
        grdLines.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        grdLines.BackgroundColor = Color.White;
        grdLines.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grdLines.Columns.AddRange(new DataGridViewColumn[] { colBarcode, colProductName, colGram, colAyar, colIsciLik, colToplam, colProductItemId, colProductCode });
        grdLines.Location = new Point(16, 165);
        grdLines.MultiSelect = false;
        grdLines.Name = "grdLines";
        grdLines.RowHeadersVisible = false;
        grdLines.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grdLines.Size = new Size(680, 320);
        grdLines.TabIndex = 3;
        grdLines.CellEndEdit += GrdLines_CellEndEdit;
        grdLines.UserDeletingRow += GrdLines_UserDeletingRow;
        //
        // colBarcode
        //
        colBarcode.HeaderText = "Barkod";
        colBarcode.Name = "colBarcode";
        colBarcode.ReadOnly = true;
        colBarcode.Width = 100;
        //
        // colProductName
        //
        colProductName.HeaderText = "Ürün Adı";
        colProductName.Name = "colProductName";
        colProductName.ReadOnly = true;
        colProductName.Width = 160;
        //
        // colGram
        //
        colGram.HeaderText = "Gram";
        colGram.Name = "colGram";
        colGram.ReadOnly = true;
        colGram.Width = 60;
        //
        // colAyar
        //
        colAyar.HeaderText = "Ayar";
        colAyar.Name = "colAyar";
        colAyar.ReadOnly = true;
        colAyar.Width = 50;
        //
        // colIsciLik
        //
        colIsciLik.HeaderText = "İşçilik Bedeli";
        colIsciLik.Name = "colIsciLik";
        colIsciLik.Width = 90;
        //
        // colToplam
        //
        colToplam.HeaderText = "Toplam Tutar";
        colToplam.Name = "colToplam";
        colToplam.ReadOnly = true;
        colToplam.Width = 100;
        //
        // colProductItemId
        //
        colProductItemId.HeaderText = "ProductItemId";
        colProductItemId.Name = "colProductItemId";
        colProductItemId.Visible = false;
        //
        // colProductCode
        //
        colProductCode.HeaderText = "ProductCode";
        colProductCode.Name = "colProductCode";
        colProductCode.Visible = false;
        //
        // pnlRight
        //
        pnlRight.Anchor = AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
        pnlRight.Controls.Add(grpPayment);
        pnlRight.Controls.Add(grpCustomer);
        pnlRight.Location = new Point(708, 165);
        pnlRight.Name = "pnlRight";
        pnlRight.Size = new Size(280, 320);
        pnlRight.TabIndex = 4;
        //
        // grpPayment
        //
        grpPayment.Controls.Add(rdoTakas);
        grpPayment.Controls.Add(rdoVeresiye);
        grpPayment.Controls.Add(rdoKrediKarti);
        grpPayment.Controls.Add(rdoNakit);
        grpPayment.Font = new Font("Segoe UI", 9F);
        grpPayment.Location = new Point(12, 12);
        grpPayment.Name = "grpPayment";
        grpPayment.Size = new Size(256, 130);
        grpPayment.TabIndex = 0;
        grpPayment.TabStop = false;
        grpPayment.Text = "Ödeme Şekli";
        //
        // rdoNakit
        //
        rdoNakit.AutoSize = true;
        rdoNakit.Checked = true;
        rdoNakit.Location = new Point(14, 28);
        rdoNakit.Name = "rdoNakit";
        rdoNakit.Size = new Size(55, 19);
        rdoNakit.TabIndex = 0;
        rdoNakit.TabStop = true;
        rdoNakit.Text = "Nakit";
        //
        // rdoKrediKarti
        //
        rdoKrediKarti.AutoSize = true;
        rdoKrediKarti.Location = new Point(14, 52);
        rdoKrediKarti.Name = "rdoKrediKarti";
        rdoKrediKarti.Size = new Size(87, 19);
        rdoKrediKarti.TabIndex = 1;
        rdoKrediKarti.Text = "Kredi Kartı";
        //
        // rdoVeresiye
        //
        rdoVeresiye.AutoSize = true;
        rdoVeresiye.Location = new Point(14, 76);
        rdoVeresiye.Name = "rdoVeresiye";
        rdoVeresiye.Size = new Size(74, 19);
        rdoVeresiye.TabIndex = 2;
        rdoVeresiye.Text = "Veresiye";
        //
        // rdoTakas
        //
        rdoTakas.AutoSize = true;
        rdoTakas.Location = new Point(14, 100);
        rdoTakas.Name = "rdoTakas";
        rdoTakas.Size = new Size(165, 19);
        rdoTakas.TabIndex = 3;
        rdoTakas.Text = "Takas (Eski altın alımı)";
        //
        // grpCustomer
        //
        grpCustomer.Controls.Add(cboCustomer);
        grpCustomer.Controls.Add(lblCustomer);
        grpCustomer.Font = new Font("Segoe UI", 9F);
        grpCustomer.Location = new Point(12, 152);
        grpCustomer.Name = "grpCustomer";
        grpCustomer.Size = new Size(256, 75);
        grpCustomer.TabIndex = 1;
        grpCustomer.TabStop = false;
        grpCustomer.Text = "Müşteri";
        //
        // cboCustomer
        //
        cboCustomer.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        cboCustomer.DropDownStyle = ComboBoxStyle.DropDown;
        cboCustomer.Location = new Point(10, 42);
        cboCustomer.Name = "cboCustomer";
        cboCustomer.Size = new Size(234, 23);
        cboCustomer.TabIndex = 1;
        cboCustomer.TextUpdate += CboCustomer_TextUpdate;
        //
        // lblCustomer
        //
        lblCustomer.AutoSize = true;
        lblCustomer.Location = new Point(10, 22);
        lblCustomer.Name = "lblCustomer";
        lblCustomer.Size = new Size(195, 15);
        lblCustomer.TabIndex = 0;
        lblCustomer.Text = "Ara ve seçin (Veresiye için zorunlu)";
        //
        // pnlSummary
        //
        pnlSummary.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        pnlSummary.BackColor = Color.FromArgb(241, 245, 249);
        pnlSummary.BorderStyle = BorderStyle.FixedSingle;
        pnlSummary.Controls.Add(lblNetTutar);
        pnlSummary.Controls.Add(lblNetCaption);
        pnlSummary.Controls.Add(lblIndirim);
        pnlSummary.Controls.Add(lblIndirimCaption);
        pnlSummary.Controls.Add(lblKdv);
        pnlSummary.Controls.Add(lblKdvCaption);
        pnlSummary.Controls.Add(lblGenelToplam);
        pnlSummary.Controls.Add(lblGenelCaption);
        pnlSummary.Controls.Add(txtIndirim);
        pnlSummary.Controls.Add(btnComplete);
        pnlSummary.Controls.Add(btnClear);
        pnlSummary.Location = new Point(16, 495);
        pnlSummary.Name = "pnlSummary";
        pnlSummary.Padding = new Padding(12);
        pnlSummary.Size = new Size(972, 95);
        pnlSummary.TabIndex = 5;
        //
        // lblGenelCaption
        //
        lblGenelCaption.AutoSize = true;
        lblGenelCaption.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        lblGenelCaption.Location = new Point(12, 18);
        lblGenelCaption.Name = "lblGenelCaption";
        lblGenelCaption.Size = new Size(95, 19);
        lblGenelCaption.TabIndex = 0;
        lblGenelCaption.Text = "Genel Toplam";
        //
        // lblGenelToplam
        //
        lblGenelToplam.Font = new Font("Segoe UI", 14F, FontStyle.Bold);
        lblGenelToplam.ForeColor = Color.FromArgb(30, 64, 175);
        lblGenelToplam.Location = new Point(180, 14);
        lblGenelToplam.Name = "lblGenelToplam";
        lblGenelToplam.Size = new Size(140, 28);
        lblGenelToplam.TabIndex = 1;
        lblGenelToplam.Text = "0,00 ₺";
        lblGenelToplam.TextAlign = ContentAlignment.MiddleRight;
        //
        // lblKdvCaption
        //
        lblKdvCaption.AutoSize = true;
        lblKdvCaption.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        lblKdvCaption.Location = new Point(12, 48);
        lblKdvCaption.Name = "lblKdvCaption";
        lblKdvCaption.Size = new Size(36, 19);
        lblKdvCaption.TabIndex = 2;
        lblKdvCaption.Text = "KDV";
        //
        // lblKdv
        //
        lblKdv.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
        lblKdv.Location = new Point(180, 44);
        lblKdv.Name = "lblKdv";
        lblKdv.Size = new Size(140, 24);
        lblKdv.TabIndex = 3;
        lblKdv.Text = "0,00 ₺";
        lblKdv.TextAlign = ContentAlignment.MiddleRight;
        //
        // lblIndirimCaption
        //
        lblIndirimCaption.AutoSize = true;
        lblIndirimCaption.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        lblIndirimCaption.Location = new Point(340, 18);
        lblIndirimCaption.Name = "lblIndirimCaption";
        lblIndirimCaption.Size = new Size(58, 19);
        lblIndirimCaption.TabIndex = 4;
        lblIndirimCaption.Text = "İndirim";
        //
        // txtIndirim
        //
        txtIndirim.Location = new Point(340, 42);
        txtIndirim.Name = "txtIndirim";
        txtIndirim.Size = new Size(90, 23);
        txtIndirim.TabIndex = 5;
        txtIndirim.Text = "0";
        txtIndirim.TextAlign = HorizontalAlignment.Right;
        txtIndirim.TextChanged += TxtIndirim_TextChanged;
        //
        // lblIndirim
        //
        lblIndirim.Font = new Font("Segoe UI", 10F);
        lblIndirim.Location = new Point(438, 44);
        lblIndirim.Name = "lblIndirim";
        lblIndirim.Size = new Size(80, 20);
        lblIndirim.TabIndex = 6;
        lblIndirim.Text = "₺";
        //
        // lblNetCaption
        //
        lblNetCaption.AutoSize = true;
        lblNetCaption.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
        lblNetCaption.Location = new Point(340, 72);
        lblNetCaption.Name = "lblNetCaption";
        lblNetCaption.Size = new Size(78, 21);
        lblNetCaption.TabIndex = 7;
        lblNetCaption.Text = "Net Tutar";
        //
        // lblNetTutar
        //
        lblNetTutar.Font = new Font("Segoe UI", 16F, FontStyle.Bold);
        lblNetTutar.ForeColor = Color.FromArgb(21, 128, 61);
        lblNetTutar.Location = new Point(520, 68);
        lblNetTutar.Name = "lblNetTutar";
        lblNetTutar.Size = new Size(200, 32);
        lblNetTutar.TabIndex = 8;
        lblNetTutar.Text = "0,00 ₺";
        lblNetTutar.TextAlign = ContentAlignment.MiddleRight;
        //
        // btnComplete
        //
        btnComplete.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        btnComplete.BackColor = Color.FromArgb(34, 197, 94);
        btnComplete.FlatStyle = FlatStyle.Flat;
        btnComplete.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        btnComplete.ForeColor = Color.White;
        btnComplete.Location = new Point(750, 28);
        btnComplete.Name = "btnComplete";
        btnComplete.Size = new Size(120, 40);
        btnComplete.TabIndex = 9;
        btnComplete.Text = "Satışı Tamamla";
        btnComplete.UseVisualStyleBackColor = false;
        btnComplete.Click += BtnComplete_Click;
        //
        // btnClear
        //
        btnClear.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        btnClear.FlatStyle = FlatStyle.Flat;
        btnClear.Font = new Font("Segoe UI", 9F);
        btnClear.Location = new Point(878, 28);
        btnClear.Name = "btnClear";
        btnClear.Size = new Size(82, 40);
        btnClear.TabIndex = 10;
        btnClear.Text = "Temizle";
        btnClear.UseVisualStyleBackColor = true;
        btnClear.Click += BtnClear_Click;
        //
        // SalesViewForm
        //
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1000, 602);
        Controls.Add(pnlSummary);
        Controls.Add(pnlRight);
        Controls.Add(grdLines);
        Controls.Add(pnlRates);
        Controls.Add(txtBarcode);
        Controls.Add(lblBarcode);
        MinimumSize = new Size(900, 550);
        Name = "SalesViewForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Satış Ekranı - Kuyumcu";
        Load += SalesViewForm_Load;
        pnlRates.ResumeLayout(false);
        pnlRates.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)grdLines).EndInit();
        pnlRight.ResumeLayout(false);
        grpPayment.ResumeLayout(false);
        grpPayment.PerformLayout();
        grpCustomer.ResumeLayout(false);
        grpCustomer.PerformLayout();
        pnlSummary.ResumeLayout(false);
        pnlSummary.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private Label lblBarcode;
    private TextBox txtBarcode;
    private Panel pnlRates;
    private Label lblRatesTitle;
    private Label lblG24;
    private Label lblG22;
    private Label lblG14;
    private Label lblRatesAge;
    private DataGridView grdLines;
    private DataGridViewTextBoxColumn colBarcode;
    private DataGridViewTextBoxColumn colProductName;
    private DataGridViewTextBoxColumn colGram;
    private DataGridViewTextBoxColumn colAyar;
    private DataGridViewTextBoxColumn colIsciLik;
    private DataGridViewTextBoxColumn colToplam;
    private DataGridViewTextBoxColumn colProductItemId;
    private DataGridViewTextBoxColumn colProductCode;
    private Panel pnlRight;
    private GroupBox grpPayment;
    private RadioButton rdoTakas;
    private RadioButton rdoVeresiye;
    private RadioButton rdoKrediKarti;
    private RadioButton rdoNakit;
    private GroupBox grpCustomer;
    private ComboBox cboCustomer;
    private Label lblCustomer;
    private Panel pnlSummary;
    private Label lblNetTutar;
    private Label lblNetCaption;
    private Label lblIndirim;
    private Label lblIndirimCaption;
    private Label lblKdv;
    private Label lblKdvCaption;
    private Label lblGenelToplam;
    private Label lblGenelCaption;
    private TextBox txtIndirim;
    private Button btnComplete;
    private Button btnClear;
}
