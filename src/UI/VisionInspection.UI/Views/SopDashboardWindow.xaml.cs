using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
        var opFilter = cmbOperator.SelectedItem as string ?? "全部";
        var wfFilter = cmbWorkflow.SelectedItem as string ?? "全部";
        var start = (dpStart.SelectedDate ?? DateTime.Today).Date;
        var end = (dpEnd.SelectedDate ?? DateTime.Today).Date.AddDays(1).AddTicks(-1);

        var filtered = _allRecords.Where(r =>
        {
            var op = string.IsNullOrWhiteSpace(r.Operator) ? "未知用户" : r.Operator;
            if (opFilter != "全部" && op != opFilter) return false;
            if (wfFilter != "全部" && r.WorkflowName != wfFilter) return false;
            if (r.EndTime < start || r.EndTime > end) return false;
            return true;
        }).ToList();

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
            v.BarWidthPx = Math.Max(3, (int)(v.Count * 280.0 / maxV));
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
            s.StdBarPx = s.StandardSec > 0 ? Math.Max(2, (int)(s.StandardSec * 280 / globalMax)) : 0;
            s.ActualBarPx = Math.Max(2, (int)(s.ActualSec * 280 / globalMax));
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
