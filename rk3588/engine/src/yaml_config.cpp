// =============================================================================
//  yaml_config.cpp  —  SOP 配方解析
// =============================================================================
//  ★ 不依赖 yaml-cpp：自带轻量解析器，只解析本项目配方实际用到的键。
//    理由：① RK3588 板端免去 yaml-cpp 交叉编译；② 对拍工具零依赖；
//          ③ 只认需要的键 → 天然"容忍未知字段"（对应 C# 的 IgnoreUnmatchedProperties）。
//
//  ★ 转换层严格对齐 SOPYamlConfig.ConvertDetectionToCondition（SOPYamlConfig.cs:713）
//    detection.method → ConditionType 的映射表必须逐项一致。
//
//  实测踩过的三个坑（都写在下方注释里，改动时勿踩）：
//   1. 顶层块切换不能只判 scope==Sop —— regions: 之后 scope 已变，后续块永远切不进去
//   2. 解析到新步骤时【不要】把 scope 改成 InStep —— 只有第一个步骤能被识别
//   3. flushStep() 内必须先调 flushDetection() —— 否则条件全丢（症状：条件数为 0）
// =============================================================================
#include "sop/yaml_config.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <sstream>
#include <vector>

namespace vis {
namespace sop {

namespace {

std::string trim(const std::string& s) {
    size_t b = s.find_first_not_of(" \t\r");
    if (b == std::string::npos) return "";
    size_t e = s.find_last_not_of(" \t\r");
    return s.substr(b, e - b + 1);
}

std::string keyOf(const std::string& line) {
    size_t p = line.find(':');
    if (p == std::string::npos) return "";
    return trim(line.substr(0, p));
}

/// 取 `key:` 后面的值（剥掉行尾注释与引号）
std::string valueAfterColon(const std::string& line) {
    size_t p = line.find(':');
    if (p == std::string::npos) return "";
    std::string v = trim(line.substr(p + 1));
    bool inQuote = false;
    for (size_t i = 0; i < v.size(); ++i) {
        if (v[i] == '"') inQuote = !inQuote;
        else if (v[i] == '#' && !inQuote) { v = trim(v.substr(0, i)); break; }
    }
    if (v.size() >= 2 && v.front() == '"' && v.back() == '"')
        v = v.substr(1, v.size() - 2);
    return v;
}

std::vector<std::string> parseInlineList(const std::string& v) {
    std::vector<std::string> out;
    std::string cur;
    bool inQuote = false;
    for (char c : v) {
        if (c == '"') { inQuote = !inQuote; continue; }
        if (c == ',' && !inQuote) {
            std::string t = trim(cur);
            if (!t.empty()) out.push_back(t);
            cur.clear();
            continue;
        }
        if (!inQuote && (c == '[' || c == ']')) continue;
        cur += c;
    }
    std::string t = trim(cur);
    if (!t.empty()) out.push_back(t);
    return out;
}

float toFloat(const std::string& s, float def = 0.0f) {
    if (s.empty()) return def;
    try { return std::stof(s); } catch (...) { return def; }
}

int toInt(const std::string& s, int def = 0) {
    if (s.empty()) return def;
    try { return std::stoi(s); } catch (...) { return def; }
}

/// 字符串 id → 稳定整数（同一配方每次运行必须得到相同映射）
int idToInt(const std::string& id, std::vector<std::string>& order) {
    for (size_t i = 0; i < order.size(); ++i)
        if (order[i] == id) return static_cast<int>(i) + 1;
    order.push_back(id);
    return static_cast<int>(order.size());
}

}  // namespace

// =============================================================================
//  detection.method → StepCondition
//  ★ 严格对齐 SOPYamlConfig.cs:713-828 的 switch
// =============================================================================
static StepCondition buildCondition(const std::string& method,
                                    const std::string& targetObject,
                                    const std::string& region,
                                    const std::string& fromRegion,
                                    const std::string& toRegion,
                                    const std::string& hand,
                                    const std::string& action,
                                    float minConfidence,
                                    int stableFrames,
                                    float tolerance,
                                    int durationMs) {

    StepCondition c;
    c.minConfidence = minConfidence;
    c.stableFrames = stableFrames;
    c.tolerance = tolerance;
    c.durationMs = durationMs;
    c.targetObject = targetObject;

    std::string m = method;
    for (char& ch : m) if (ch >= 'A' && ch <= 'Z') ch = static_cast<char>(ch - 'A' + 'a');

    if (m == "hand_in_region") {
        c.type = ConditionType::HandInRegion;
        c.targetObject = "hand";
        c.zoneId = region;
        c.parameters["HandSide"] = hand.empty() ? "right" : hand;
    }
    else if (m == "hand_not_in_region") {
        c.type = ConditionType::HandNotInRegion;
        c.targetObject = "hand";
        c.zoneId = region;
        c.parameters["HandSide"] = hand.empty() ? "right" : hand;
    }
    else if (m == "hand_stable") {
        c.type = ConditionType::HandStable;
        c.targetObject = "hand";
        c.zoneId = region;
        c.parameters["HandSide"] = hand.empty() ? "right" : hand;
        c.parameters["Tolerance"] = std::to_string(tolerance);
    }
    else if (m == "hand_move") {
        c.type = ConditionType::HandMoveFromTo;
        c.parameters["HandSide"] = hand.empty() ? "right" : hand;
        c.parameters["FromRegion"] = fromRegion;
        c.parameters["ToRegion"] = toRegion;
        c.fromRegion = fromRegion;
        c.toRegion = toRegion;
    }
    else if (m == "hand_near_object") {
        c.type = ConditionType::HandNearObject;
        c.targetObject = targetObject;
        c.parameters["HandSide"] = hand.empty() ? "right" : hand;
        c.parameters["Margin"] = std::to_string(tolerance);
    }
    else if (m == "object_present") {
        c.type = ConditionType::ObjectPresent;
        c.targetObject = targetObject;
    }
    else if (m == "object_in_zone") {
        c.type = ConditionType::ObjectInZone;
        c.targetObject = targetObject;
        c.zoneId = region;
    }
    else if (m == "pose_stable") {
        c.type = ConditionType::ObjectStable;
        c.targetObject = targetObject;
        c.parameters["Tolerance"] = std::to_string(tolerance);
    }
    else if (m == "time_elapsed") {
        c.type = ConditionType::TimeElapsed;
        // ★ C# 用 Parameters["RequiredSeconds"] = duration_ms / 1000
        c.parameters["RequiredSeconds"] = std::to_string(durationMs / 1000.0);
    }
    else if (m == "person_present") {
        c.type = ConditionType::ObjectPresent;
        c.targetObject = targetObject.empty() ? "person" : targetObject;
    }
    else if (m == "hand_action") {
        c.type = ConditionType::HandMoveFromTo;
        std::string act = action;
        for (char& ch : act) if (ch >= 'A' && ch <= 'Z') ch = static_cast<char>(ch - 'A' + 'a');
        if (act == "pickup") {
            // 拿起：只看 FromRegion（手曾在此 → 现已离开）
            c.parameters["FromRegion"] = fromRegion;
            c.parameters["ToRegion"] = "";
            c.fromRegion = fromRegion;
            c.toRegion = "";
            c.targetObject = targetObject;
        }
        else if (act == "putdown") {
            // ★ 放下：保留 FromRegion（避免"杯子本来就在桌面"被误判为已放下）
            c.parameters["FromRegion"] = fromRegion;
            c.parameters["ToRegion"] = toRegion;
            c.fromRegion = fromRegion;
            c.toRegion = toRegion;
            c.targetObject = targetObject;
        }
        else {
            c.parameters["FromRegion"] = fromRegion;
            c.parameters["ToRegion"] = toRegion;
            c.fromRegion = fromRegion;
            c.toRegion = toRegion;
            c.targetObject = targetObject;
        }
        c.parameters["HandSide"] = hand.empty() ? "any" : hand;
        c.parameters["Action"] = act;
    }
    else {
        // 不支持的方法：设为 Custom，对拍时会暴露，不会静默通过
        c.type = ConditionType::Custom;
    }

    return c;
}

// =============================================================================
//  loadRecipe —— 主解析（缩进驱动的状态机）
// =============================================================================
bool loadRecipe(const std::string& yamlPath,
                SOPWorkflow& outWorkflow,
                std::vector<ZoneDefinition>& outZones) {

    std::ifstream fin(yamlPath);
    if (!fin) {
        std::fprintf(stderr, "[yaml] 打不开: %s\n", yamlPath.c_str());
        return false;
    }

    struct Line { int indent; std::string text; };
    std::vector<Line> lines;
    {
        std::string raw;
        while (std::getline(fin, raw)) {
            std::string t = trim(raw);
            if (t.empty() || t[0] == '#') continue;
            Line l;
            size_t ind = raw.find_first_not_of(" \t");
            l.indent = (ind == std::string::npos) ? 0 : static_cast<int>(ind);
            l.text = raw.substr(l.indent);
            lines.push_back(l);
        }
    }

    std::vector<std::string> stepIdOrder;

    // settings 段字段（C# SOPGlobalSettings）
    bool  enableTimeoutDetection = true;
    bool  enableSkipDetection = true;
    int   globalStable = 5;
    float globalConf = 0.6f;
    bool  autoReset = true;
    int   resetDelay = 1;

    std::string curZoneId, curZoneName;
    float zx1 = 0, zy1 = 0, zx2 = 0, zy2 = 0;
    bool  inZone = false;

    SOPStep curStep;
    bool  haveStep = false;
    bool  inDetection = false;

    std::string dMethod, dTarget, dRegion, dFrom, dTo, dHand, dAction;
    float dMinConf = 0.6f, dTol = 20.0f;
    int   dStable = 3, dDuration = 0;

    auto flushZone = [&]() {
        if (!inZone) return;
        ZoneDefinition z;
        z.id = curZoneId;
        z.name = curZoneName;
        z.x = zx1; z.y = zy1;
        z.width = zx2 - zx1;
        z.height = zy2 - zy1;
        outZones.push_back(z);
        inZone = false;
    };

    // ★ 声明顺序：flushDetection 在前，flushStep 内会调用它
    auto flushDetection = [&]() {
        if (!inDetection) return;
        if (!dMethod.empty()) {
            curStep.passConditions.push_back(
                buildCondition(dMethod, dTarget, dRegion, dFrom, dTo,
                               dHand, dAction, dMinConf, dStable, dTol, dDuration));
        }
        inDetection = false;
    };

    auto flushStep = [&]() {
        if (!haveStep) return;
        flushDetection();          // ★ 先收尾 detection，否则条件全丢
        outWorkflow.steps.push_back(curStep);
        haveStep = false;
        curStep = SOPStep();
        dMethod.clear(); dTarget.clear(); dRegion.clear();
        dFrom.clear(); dTo.clear(); dHand.clear(); dAction.clear();
        dMinConf = 0.6f; dTol = 20.0f; dStable = 3; dDuration = 0;
    };

    // ★ settings 解析状态（全局段）
    bool  inSettings = false;

    enum class Scope { None, Sop, Regions, Steps, Settings };
    Scope scope = Scope::None;

    for (const auto& l : lines) {
        const std::string& t = l.text;
        const std::string key = keyOf(t);
        const std::string val = valueAfterColon(t);

        // ---- 顶层 sop: ----
        if (l.indent == 0) {
            if (key == "sop") { flushStep(); scope = Scope::Sop; continue; }
            scope = Scope::None;
            continue;
        }

        // ---- 顶层块切换（indent==2 的 map 键）----
        // ★ 未识别的块不能清 scope 成 None，否则后续 regions:/steps: 会被跳过
        if (l.indent == 2 && val.empty() && !key.empty() && scope != Scope::None) {
            if (key == "regions")  { flushZone(); flushStep(); scope = Scope::Regions; continue; }
            if (key == "steps")    { flushZone(); flushStep(); scope = Scope::Steps;   continue; }
            if (key == "settings") { flushZone(); flushStep(); inSettings = true; scope = Scope::Settings; continue; }
            if (scope == Scope::Regions || scope == Scope::Steps || scope == Scope::Settings) {
                flushZone(); flushStep();
            }
            scope = Scope::Sop;
            continue;
        }

        // ================= settings（全局）=================
        if (scope == Scope::Settings) {
            if (l.indent >= 4 && !key.empty()) {
                if (key == "enableTimeoutDetection") enableTimeoutDetection = (val == "true");
                else if (key == "enableSkipDetection") enableSkipDetection = (val == "true");
                else if (key == "stableFrames") globalStable = toInt(val, 5);
                else if (key == "confidenceThreshold") globalConf = toFloat(val, 0.6f);
                else if (key == "auto_reset_on_complete") autoReset = (val == "true");
                else if (key == "reset_delay_sec") resetDelay = toInt(val, 1);
            }
            continue;
        }

        // ================= regions =================
        if (scope == Scope::Regions) {
            if (l.indent == 4 && !key.empty() && val.empty()) {
                flushZone();
                inZone = true;
                curZoneId = key;
                curZoneName.clear();
                zx1 = zy1 = zx2 = zy2 = 0;
                continue;
            }
            if (inZone && l.indent >= 6) {
                if (key == "x1") zx1 = toFloat(val);
                else if (key == "y1") zy1 = toFloat(val);
                else if (key == "x2") zx2 = toFloat(val);
                else if (key == "y2") zy2 = toFloat(val);
                else if (key == "name") curZoneName = val;
            }
            continue;
        }

        // ================= steps =================
        if (scope == Scope::Steps) {
            // 新步骤：`- id: "xxx"`
            // ★ 不改 scope —— 否则后续 "- id" 行不再命中本分支，只有第一个步骤能被解析
            if (l.indent == 4 && t.rfind("- ", 0) == 0) {
                flushStep();
                std::string rest = trim(t.substr(2));
                if (keyOf(rest) == "id") {
                    haveStep = true;
                    curStep.stepId = idToInt(valueAfterColon(rest), stepIdOrder);
                }
                continue;
            }
            // 容错：无 "- " 前缀但出现 id:
            if (l.indent == 4 && key == "id") {
                flushStep();
                haveStep = true;
                curStep.stepId = idToInt(val, stepIdOrder);
                continue;
            }
            if (!haveStep) continue;

            if (key == "detection" && l.indent == 6) {
                flushDetection();
                inDetection = true;
                continue;
            }

            if (l.indent == 6 && !inDetection) {
                if (key == "name") curStep.stepName = val;
                else if (key == "description") curStep.description = val;
                else if (key == "timeout") curStep.timeoutSec = toInt(val, 30);
                else if (key == "timeout_sec") curStep.timeoutSec = toInt(val, 30);
                else if (key == "standard_duration_ms")
                    curStep.standardDurationSec = toFloat(val) / 1000.0;
                else if (key == "transitions") curStep.nextSteps = parseInlineList(val);
                else if (key == "camera") curStep.cameraId = val;
                else if (key == "model") curStep.modelPath = val;
                continue;
            }

            if (inDetection && l.indent >= 8) {
                if (key == "method") dMethod = val;
                else if (key == "target_object") dTarget = val;
                else if (key == "region") dRegion = val;
                else if (key == "from_region") dFrom = val;
                else if (key == "to_region") dTo = val;
                else if (key == "hand") dHand = val;
                else if (key == "action") dAction = val;
                else if (key == "min_confidence") dMinConf = toFloat(val, 0.6f);
                else if (key == "stable_frames") dStable = toInt(val, 3);
                else if (key == "tolerance") dTol = toFloat(val, 20.0f);
                else if (key == "duration_ms") dDuration = toInt(val, 0);
            }
            continue;
        }
    }

    flushStep();
    flushZone();

    if (outWorkflow.steps.empty()) {
        std::fprintf(stderr, "[yaml] 未解析到任何步骤: %s\n", yamlPath.c_str());
        return false;
    }

    outWorkflow.name = "SOP";
    for (const auto& z : outZones) outWorkflow.zones[z.id] = z;

    // 全局设置回填（★ enableTimeoutDetection=false 时不得产生 Timeout 违规）
    outWorkflow.settings.enableTimeoutDetection = enableTimeoutDetection;
    outWorkflow.settings.enableSkipDetection = enableSkipDetection;
    outWorkflow.settings.stableFrameCount = globalStable;
    outWorkflow.settings.confidenceThreshold = globalConf;
    outWorkflow.settings.autoResetOnComplete = autoReset;
    outWorkflow.settings.resetDelaySec = resetDelay;

    std::fprintf(stderr, "[yaml] 解析完成: %zu 步 %zu 区域 timeout=%d skip=%d stable=%d conf=%.2f\n",
                 outWorkflow.steps.size(), outZones.size(),
                 (int)outWorkflow.settings.enableTimeoutDetection,
                 (int)outWorkflow.settings.enableSkipDetection,
                 outWorkflow.settings.stableFrameCount,
                 outWorkflow.settings.confidenceThreshold);
    for (const auto& s : outWorkflow.steps) {
        std::fprintf(stderr, "  步骤 %d '%s' 条件数=%zu next=[",
                     s.stepId, s.stepName.c_str(), s.passConditions.size());
        for (const auto& n : s.nextSteps) std::fprintf(stderr, "%s ", n.c_str());
        std::fprintf(stderr, "] 条件: ");
        for (const auto& c : s.passConditions) std::fprintf(stderr, "%s ", toString(c.type));
        std::fprintf(stderr, "\n");
    }

    return true;
}

}  // namespace sop
}  // namespace vis