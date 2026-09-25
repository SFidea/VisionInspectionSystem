using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using VisionInspection.Modules.SOP;
using VisionInspection.Modules.SOP.Models;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// SOP 运行数据记录器。
/// 订阅 SOPModule.WorkflowCompleted（每跑完一个完整周期触发一次），把该周期的运行情况落盘到「按天文件」：
///   - sop_records/sop_run_YYYY-MM-DD.jsonl ：每行一个周期的完整结构化记录（OK/NG、总耗时、每步耗时、违规明细）
///   - sop_records/sop_run_YYYY-MM-DD.csv  ：长表，每步一行，方便用 Excel/脚本做透视统计
///
/// 记录内容满足三类需求：
///   1) 每个 SOP 当天的运行情况（按 WorkflowName 归类，文件按天切分）
///   2) 每个周期是 OK 还是 NG（HasViolations 决定）
///   3) 每个周期的每个步骤用了多少秒（StepHistory 中每步 Duration）
///
/// 文件位于 <程序基目录>/sop_records/。写入失败仅吞异常，不影响检测主流程。
/// </summary>
public class SopRunRecorder
{
    private readonly string _recordsDir;
    private readonly object _writeLock = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

    public SopRunRecorder(string? baseDir = null)
    {
        _recordsDir = Path.Combine(baseDir ?? AppDomain.CurrentDomain.BaseDirectory, "sop_records");
        try { Directory.CreateDirectory(_recordsDir); } catch { }
    }

    public void Subscribe(SOPModule module)
    {
        module.WorkflowCompleted += OnWorkflowCompleted;
    }

    public void Unsubscribe(SOPModule module)
    {
        module.WorkflowCompleted -= OnWorkflowCompleted;
    }

    private void OnWorkflowCompleted(object? sender, SOPCompletedEventArgs e)
    {
        try
        {
            if (e.StepHistory == null || e.StepHistory.Count == 0) return;

            var startTime = e.StepHistory.First().StartTime;
            var endTime = e.StepHistory.Last().EndTime;
            var result = e.HasViolations ? "NG" : "OK";

            var record = new SopCycleRecord
            {
                StartTime = startTime,
                EndTime = endTime,
                WorkflowName = e.WorkflowName,
                WorkflowId = e.WorkflowId,
                Operator = e.Operator,
                Result = result,
                TotalDurationSec = Math.Round((endTime - startTime).TotalSeconds, 2),
                StepCount = e.StepHistory.Count,
                Steps = e.StepHistory.Select(s => new SopStepDuration
                {
                    StepId = s.StepId,
                    StepName = s.StepName,
                    DurationSec = Math.Round(s.Duration.TotalSeconds, 2),
                    StandardDurationSec = Math.Round(s.StandardDurationSec, 2)
                }).ToList(),
                Violations = e.Violations.Select(v => new SopViolationSummary
                {
                    Type = v.Type.ToString(),
                    StepId = v.StepId,
                    Description = v.Description
                }).ToList()
            };

            AppendRecord(record);
        }
        catch
        {
            // 记录失败不影响检测主流程
        }
    }

    private void AppendRecord(SopCycleRecord record)
    {
        var dateStr = record.EndTime.ToString("yyyy-MM-dd");
        var jsonlPath = Path.Combine(_recordsDir, $"sop_run_{dateStr}.jsonl");
        var csvPath = Path.Combine(_recordsDir, $"sop_run_{dateStr}.csv");

        lock (_writeLock)
        {
            // 当天周期序号（第几次），跨重启也能连续（基于已有行数）
            int sequence = 1;
            if (File.Exists(jsonlPath))
            {
                try { sequence = File.ReadAllLines(jsonlPath).Length + 1; } catch { }
            }
            record.CycleIndex = sequence;

            // 1) JSONL：一行一个周期
            var jsonLine = JsonSerializer.Serialize(record, _jsonOptions);
            File.AppendAllText(jsonlPath, jsonLine + Environment.NewLine);

            // 2) CSV：长表，每步一行（首行不存在或为空时写表头）
            bool csvNew = !File.Exists(csvPath) || new FileInfo(csvPath).Length == 0;
            var sb = new StringBuilder();
            if (csvNew)
            {
                sb.AppendLine("CycleIndex,CycleTime,Operator,WorkflowName,Result,TotalDurationSec,StepIndex,StepId,StepName,StepDurationSec,StandardDurationSec,ViolationCount,ViolationTypes");
            }
            var violationTypes = string.Join("|", record.Violations.Select(v => v.Type));
            for (int i = 0; i < record.Steps.Count; i++)
            {
                var s = record.Steps[i];
                sb.AppendLine(string.Join(",",
                    CsvField(sequence.ToString()),
                    CsvField(record.EndTime.ToString("yyyy-MM-dd HH:mm:ss")),
                    CsvField(record.Operator),
                    CsvField(record.WorkflowName),
                    CsvField(record.Result),
                    CsvField(record.TotalDurationSec.ToString("F2")),
                    CsvField((i + 1).ToString()),
                    CsvField(s.StepId.ToString()),
                    CsvField(s.StepName),
                    CsvField(s.DurationSec.ToString("F2")),
                    CsvField(s.StandardDurationSec.ToString("F2")),
                    CsvField(record.Violations.Count.ToString()),
                    CsvField(violationTypes)));
            }
            File.AppendAllText(csvPath, sb.ToString());
        }
    }

    /// <summary>CSV 字段转义：含逗号/引号/换行时用双引号包裹并转义内部引号。</summary>
    private static string CsvField(string v) =>
        string.IsNullOrEmpty(v)
            ? "\"\""
            : (v.Contains(',') || v.Contains('"') || v.Contains('\n')
                ? "\"" + v.Replace("\"", "\"\"") + "\""
                : v);
}

/// <summary>一个完整周期（一次 SOP 跑完）的运行记录。</summary>
public class SopCycleRecord
{
    /// <summary>当天第几次运行（从 1 开始，跨重启连续）。</summary>
    public int CycleIndex { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    /// <summary>所属 SOP 流程名称。</summary>
    public string WorkflowName { get; set; } = "";
    public string WorkflowId { get; set; } = "";
    /// <summary>操作人（登录用户名）；改动前产生的历史数据该字段为空（归为"未知用户"）。</summary>
    public string Operator { get; set; } = "";
    /// <summary>"OK" 或 "NG"。</summary>
    public string Result { get; set; } = "";
    /// <summary>周期总耗时（秒）。</summary>
    public double TotalDurationSec { get; set; }
    public int StepCount { get; set; }
    public List<SopStepDuration> Steps { get; set; } = new();
    public List<SopViolationSummary> Violations { get; set; } = new();
}

    /// <summary>周期内单步耗时记录。</summary>
    public class SopStepDuration
    {
        public int StepId { get; set; }
        public string StepName { get; set; } = "";
        /// <summary>该步骤耗时（秒）。</summary>
        public double DurationSec { get; set; }
        /// <summary>该步骤标准耗时（秒），来自配方 standard_duration_ms（缺省回退 duration_ms）。</summary>
        public double StandardDurationSec { get; set; }
    }

/// <summary>导致 NG 的违规摘要。</summary>
public class SopViolationSummary
{
    public string Type { get; set; } = "";
    public int StepId { get; set; }
    public string Description { get; set; } = "";
}
