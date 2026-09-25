using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VisionInspection.Modules.SOP.Services;

namespace VisionInspection.UI.Views;

/// <summary>
/// SOP 检测数据看板：根据登录用户名（操作员）、产品配方（SOP 流程名）、日期，
/// 读取 sop_records/ 下按天记录的运行数据，分析 OK/NG、违规步骤排行、每步标准 vs 实际耗时。
/// </summary>
public partial class SopDashboardWindow : Window
{
    private readonly string _recordsDir;
    private List<SopCycleRecord> _allRecords = new();

    public SopDashboardWindow()
    {
        InitializeComponent();
        _recordsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sop_records");
        dpStart.SelectedDate = DateTime.Today;
        dpEnd.SelectedDate = DateTime.Today;
        LoadFilters();
        RunQuery();
    }

    private void BtnQuery_Click(object sender, RoutedEventArgs e) => RunQuery();

    /// <summary>扫描 sop_records/ 下所有 jsonl，构建操作员/配方下拉项。</summary>
    private void LoadFilters()
    {
        _allRecords = LoadAllRecords();

        var operators = new List<string> { "全部" };
        foreach (var op in _allRecords
                     .Select(r => string.IsNullOrWhiteSpace(r.Operator) ? "未知用户" : r.Operator)
                     .Distinct().OrderBy(x => x))
            operators.Add(op);
        cmbOperator.ItemsSource = operators;

        var workflows = new List<string> { "全部" };
        foreach (var wf in _allRecords
                     .Select(r => r.WorkflowName)
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct().OrderBy(x => x))
            workflows.Add(wf);
        cmbWorkflow.ItemsSource = workflows;

        cmbOperator.SelectedIndex = 0;
        cmbWorkflow.SelectedIndex = 0;
    }

    private List<SopCycleRecord> LoadAllRecords()
    {
        var list = new List<SopCycleRecord>();
        if (!Directory.Exists(_recordsDir)) return list;
        var files = Directory.GetFiles(_recordsDir, "sop_run_*.jsonl");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var f in files)
        {
            try
            {
                foreach (var line in File.ReadAllLines(f))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var rec = JsonSerializer.Deserialize<SopCycleRecord>(line, options);
                    if (rec != null) list.Add(rec);
                }
            }
            catch { /* 单文件损坏不影响其它文件 */ }
        }
        return list;
    }

    private void RunQuery()
    {
        var filtered = GetFilteredRecords();

        // ===== KPI =====
        int total = filtered.Count;
        int ok = filtered.Count(r => r.Result == "OK");
        int ng = total - ok;
        double okRate = total > 0 ? (double)ok / total * 100 : 0;
        double avgTotal = total > 0 ? filtered.Average(r => r.TotalDurationSec) : 0;

        var cyclesWithStd = filtered.Where(r => r.Steps.Sum(s => s.StandardDurationSec) > 0).ToList();
        double avgStd = cyclesWithStd.Count > 0
            ? cyclesWithStd.Average(r => r.Steps.Sum(s => s.StandardDurationSec)) : 0;
        double deviation = avgStd > 0 ? avgTotal - avgStd : 0;

        txtTotal.Text = total.ToString();
        txtOk.Text = ok.ToString();
        txtNg.Text = ng.ToString();
        txtOkRate.Text = $"{okRate:F1}%";
        txtAvgTotal.Text = $"{avgTotal:F1}s";
        txtAvgStd.Text = avgStd > 0 ? $"{avgStd:F1}s" : "—";
        txtDeviation.Text = avgStd > 0 ? $"{(deviation >= 0 ? "+" : "")}{deviation:F1}s" : "—";

        // ===== 违规步骤排行 =====
        var violationGroups = filtered
            .SelectMany(r => r.Violations.Select(v => new { Rec = r, V = v }))
            .GroupBy(x => x.V.StepId)
            .Select(g =>
            {
                var stepName = g.First().Rec.Steps.FirstOrDefault(s => s.StepId == g.Key)?.StepName;
                if (string.IsNullOrWhiteSpace(stepName))
                    stepName = g.First().Rec.Steps.FirstOrDefault()?.StepName ?? $"步骤{g.Key}";
                return new ViolationRankRow
                {
                    StepId = g.Key,
                    Label = $"#{g.Key} {stepName}",
                    Count = g.Count()
                };
            })
            .OrderByDescending(x => x.Count)
            .ToList();

        int maxV = violationGroups.Count > 0 ? violationGroups.Max(x => x.Count) : 1;
        foreach (var v in violationGroups)
            v.BarWidthPx = Math.Max(3, (int)(v.Count * 240.0 / maxV));
        icViolations.ItemsSource = violationGroups;
        txtNoViolation.Visibility = violationGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // ===== 各步骤 标准 vs 实际 平均耗时 =====
        var stepGroups = filtered
            .SelectMany(r => r.Steps.Select(s => new { Rec = r, S = s }))
            .GroupBy(x => x.S.StepId)
            .Select(g => new StepCompareRow
            {
                StepId = g.Key,
                StepName = g.First().S.StepName,
                StandardSec = g.Average(x => x.S.StandardDurationSec),
                ActualSec = g.Average(x => x.S.DurationSec)
            })
            .OrderBy(x => x.StepId)
            .ToList();

        double globalMax = stepGroups.Count > 0 ? stepGroups.Max(x => Math.Max(x.StandardSec, x.ActualSec)) : 1;
        if (globalMax <= 0) globalMax = 1;
        foreach (var s in stepGroups)
        {
            s.StdBarPx = s.StandardSec > 0 ? Math.Max(2, (int)(s.StandardSec * 220 / globalMax)) : 0;
            s.ActualBarPx = Math.Max(2, (int)(s.ActualSec * 220 / globalMax));
            s.StandardText = s.StandardSec > 0 ? $"标准 {s.StandardSec:F1}s" : "标准 —";
            s.ActualText = $"实际 {s.ActualSec:F1}s";
            double dev = s.ActualSec - s.StandardSec;
            s.DeviationText = s.StandardSec > 0 ? $"偏差 {(dev >= 0 ? "+" : "")}{dev:F1}s" : "";
            s.DeviationColor = dev > 0.05 ? "#E05353" : (dev < -0.05 ? "#4FA86A" : "#6B7280");
        }
        icStepCompare.ItemsSource = stepGroups;
        txtNoStep.Visibility = stepGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // ===== 周期明细 =====
        var rows = filtered.OrderBy(r => r.EndTime).Select(r => new CycleRow
        {
            Time = r.EndTime.ToString("MM-dd HH:mm"),
            Operator = string.IsNullOrWhiteSpace(r.Operator) ? "未知" : r.Operator,
            WorkflowName = r.WorkflowName,
            Result = r.Result,
            TotalDurationSec = r.TotalDurationSec.ToString("F1"),
            StandardTotalSec = r.Steps.Sum(s => s.StandardDurationSec) > 0
                ? r.Steps.Sum(s => s.StandardDurationSec).ToString("F1") : "—",
            DeviationSec = r.Steps.Sum(s => s.StandardDurationSec) > 0
                ? (r.TotalDurationSec - r.Steps.Sum(s => s.StandardDurationSec)).ToString("+0.0;-0.0") : "—"
        }).ToList();
        dgCycles.ItemsSource = rows;
    }

    // ===== 筛选 / 完整性检查 / 导出 CSV =====

    /// <summary>按当前筛选条件（操作员 / 配方 / 日期区间）返回过滤后的周期记录。</summary>
    private List<SopCycleRecord> GetFilteredRecords()
    {
        var opFilter = cmbOperator.SelectedItem as string ?? "全部";
        var wfFilter = cmbWorkflow.SelectedItem as string ?? "全部";
        var start = (dpStart.SelectedDate ?? DateTime.Today).Date;
        var end = (dpEnd.SelectedDate ?? DateTime.Today).Date.AddDays(1).AddTicks(-1);

        return _allRecords.Where(r =>
        {
            var op = string.IsNullOrWhiteSpace(r.Operator) ? "未知用户" : r.Operator;
            if (opFilter != "全部" && op != opFilter) return false;
            if (wfFilter != "全部" && r.WorkflowName != wfFilter) return false;
            if (r.EndTime < start || r.EndTime > end) return false;
            return true;
        }).ToList();
    }

    private void BtnCheck_Click(object sender, RoutedEventArgs e)
    {
        var filtered = GetFilteredRecords();
        if (filtered.Count == 0)
        {
            txtStatus.Text = "当前筛选条件下无记录可检查。";
            return;
        }
        var report = CheckCompleteness(filtered);
        MessageBox.Show(report, "检测记录完整性检查", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var filtered = GetFilteredRecords();
        if (filtered.Count == 0)
        {
            txtStatus.Text = "当前筛选条件下无记录，无法导出。";
            return;
        }

        // 导出前做完整性预检：若有不完整周期，提示用户是否仍导出
        var incomplete = filtered.Count(r => !IsRecordComplete(r));
        if (incomplete > 0)
        {
            var report = CheckCompleteness(filtered);
            var ans = MessageBox.Show(
                $"检测到 {incomplete} 条周期记录不完整（步骤数据可能缺失）。\n是否仍要导出 CSV？\n\n{report}",
                "导出确认 - 记录不完整", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ans != MessageBoxResult.Yes) return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "导出检测记录 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            FileName = $"sop_export_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            DefaultExt = ".csv",
            AddExtension = true
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            WriteCsv(dlg.FileName, filtered);
            txtStatus.Text = $"已导出 {filtered.Count} 个周期 / {filtered.Sum(r => r.Steps.Count)} 步 → {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"导出失败：{ex.Message}";
            MessageBox.Show($"导出失败：{ex.Message}", "导出错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>按配方分组，检查每个配方下每条周期记录是否完整保存。</summary>
    private string CheckCompleteness(List<SopCycleRecord> records)
    {
        var groups = records
            .GroupBy(r => string.IsNullOrWhiteSpace(r.WorkflowName) ? "(未命名配方)" : r.WorkflowName)
            .OrderBy(g => g.Key);

        var sb = new StringBuilder();
        int totalCycles = records.Count;
        int incompleteTotal = 0;
        int recipeCount = 0;
        foreach (var g in groups)
        {
            recipeCount++;
            int cyc = g.Count();
            int incomplete = g.Count(r => !IsRecordComplete(r));
            incompleteTotal += incomplete;
            sb.AppendLine($"• {g.Key}：共 {cyc} 周期，完整 {cyc - incomplete}，缺失/不完整 {incomplete}");
        }

        sb.Insert(0, $"完整性检查（共 {totalCycles} 周期，跨 {recipeCount} 个配方）：\n");
        sb.AppendLine(incompleteTotal == 0
            ? "\n✓ 所有配方检测记录均完整保存。"
            : $"\n⚠ 共有 {incompleteTotal} 条周期记录不完整（步骤数据缺失或步骤数不匹配）。");
        return sb.ToString();
    }

    /// <summary>单条周期记录是否完整：步骤非空、步骤数与声明一致、各步耗时有效。</summary>
    private bool IsRecordComplete(SopCycleRecord r)
    {
        if (r.Steps == null || r.Steps.Count == 0) return false;
        if (r.StepCount > 0 && r.Steps.Count != r.StepCount) return false;
        if (r.Steps.Any(s => s.DurationSec < 0)) return false;
        return true;
    }

    /// <summary>将过滤后的周期记录（步骤级明细）写出为 CSV（含 BOM，便于 Excel 正确显示中文）。</summary>
    private void WriteCsv(string path, List<SopCycleRecord> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CycleIndex,CycleTime,Operator,WorkflowName,Result,TotalDurationSec,StepIndex,StepId,StepName,StepDurationSec,StandardDurationSec,ViolationCount,ViolationTypes");

        foreach (var rec in records.OrderBy(r => r.EndTime))
        {
            var violationTypes = string.Join("|", rec.Violations.Select(v => v.Type));
            var op = string.IsNullOrWhiteSpace(rec.Operator) ? "未知用户" : rec.Operator;
            for (int i = 0; i < rec.Steps.Count; i++)
            {
                var s = rec.Steps[i];
                sb.AppendLine(string.Join(",",
                    CsvField(rec.CycleIndex.ToString()),
                    CsvField(rec.EndTime.ToString("yyyy-MM-dd HH:mm:ss")),
                    CsvField(op),
                    CsvField(rec.WorkflowName),
                    CsvField(rec.Result),
                    CsvField(rec.TotalDurationSec.ToString("F2")),
                    CsvField((i + 1).ToString()),
                    CsvField(s.StepId.ToString()),
                    CsvField(s.StepName),
                    CsvField(s.DurationSec.ToString("F2")),
                    CsvField(s.StandardDurationSec.ToString("F2")),
                    CsvField(rec.Violations.Count.ToString()),
                    CsvField(violationTypes)));
            }
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>CSV 字段转义：含逗号/引号/换行时用双引号包裹并转义内部引号。</summary>
    private static string CsvField(string v) =>
        string.IsNullOrEmpty(v)
            ? "\"\""
            : (v.Contains(',') || v.Contains('"') || v.Contains('\n')
                ? "\"" + v.Replace("\"", "\"\"") + "\""
                : v);
}

public class ViolationRankRow
{
    public int StepId { get; set; }
    public string Label { get; set; } = "";
    public int Count { get; set; }
    public int BarWidthPx { get; set; }
}

public class StepCompareRow
{
    public int StepId { get; set; }
    public string StepName { get; set; } = "";
    public double StandardSec { get; set; }
    public double ActualSec { get; set; }
    public int StdBarPx { get; set; }
    public int ActualBarPx { get; set; }
    public string StandardText { get; set; } = "";
    public string ActualText { get; set; } = "";
    public string DeviationText { get; set; } = "";
    public string DeviationColor { get; set; } = "#6B7280";
}

public class CycleRow
{
    public string Time { get; set; } = "";
    public string Operator { get; set; } = "";
    public string WorkflowName { get; set; } = "";
    public string Result { get; set; } = "";
    public string TotalDurationSec { get; set; } = "";
    public string StandardTotalSec { get; set; } = "";
    public string DeviationSec { get; set; } = "";
}
