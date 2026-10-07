// =============================================================================
//  replay_main.cpp  —  黄金回放对拍程序（P1 验收工具）
// =============================================================================
//  用途：读 Windows 版录制的 inputs.jsonl，逐帧喂给【本机 C++ SOP 引擎】，
//        产出 decisions.jsonl，再由 inspect_golden.py compare 与 Windows 基线比对。
//
//  这是"SOP 检测 100% 保留"的可验证方式：不动板子、不装 RKNN，
//  纯 PC 就能验证引擎移植是否逐帧一致。
//
//  用法：
//    replay_main <inputs.jsonl> <out_decisions.jsonl> [recipe.yaml]
// =============================================================================
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>

#include "sop/state_machine.h"
#include "sop/yaml_config.h"
#include "sop/types.h"

using namespace vis;
using namespace vis::sop;

namespace {

// ============================================================ 极简 JSON 取值
// inputs.jsonl 由 Python 生成，结构固定，无需完整 JSON 解析器。

std::vector<Detection> parseObjects(const std::string& line) {
    std::vector<Detection> out;
    size_t p = line.find("\"objects\":[");
    if (p == std::string::npos) return out;
    p += 11;
    while (true) {
        size_t e = line.find('}', p);
        if (e == std::string::npos) break;
        std::string obj = line.substr(p, e - p);
        if (obj.find("\"label\"") == std::string::npos) break;

        Detection d;
        size_t q = obj.find("\"label\":\"");
        if (q != std::string::npos) {
            q += 9;
            size_t qe = obj.find('"', q);
            d.className = obj.substr(q, qe - q);
        }
        q = obj.find("\"conf\":");
        if (q != std::string::npos) d.score = static_cast<float>(std::atof(obj.c_str() + q + 7));
        q = obj.find("\"box\":[");
        if (q != std::string::npos) {
            double b[4];
            if (std::sscanf(obj.c_str() + q + 6, "%lf,%lf,%lf,%lf", &b[0], &b[1], &b[2], &b[3]) == 4) {
                d.box.x = static_cast<float>(b[0]);
                d.box.y = static_cast<float>(b[1]);
                d.box.w = static_cast<float>(b[2] - b[0]);
                d.box.h = static_cast<float>(b[3] - b[1]);
            }
        }
        out.push_back(d);
        p = e + 1;
        if (p < line.size() && line[p] == ',') { p++; continue; }
        break;
    }
    return out;
}

FrameDetections parseHands(const std::string& line, int width, int height) {
    FrameDetections fd;
    fd.width = width;
    fd.height = height;

    size_t p = line.find("\"hands\":[");
    if (p == std::string::npos) return fd;
    p += 9;

    while (true) {
        size_t e = line.find('}', p);
        if (e == std::string::npos) break;
        std::string obj = line.substr(p, e - p);
        if (obj.find("\"track_id\"") == std::string::npos) break;

        HandResult h;
        size_t q = obj.find("\"track_id\":");
        if (q != std::string::npos) h.trackId = std::atoi(obj.c_str() + q + 11);

        q = obj.find("\"is_left\":");
        if (q != std::string::npos)
            h.isLeft = (obj.compare(q + 10, 4, "true") == 0);

        q = obj.find("\"valid_points\":");
        if (q != std::string::npos) {
            int n = std::atoi(obj.c_str() + q + 14);
            h.validPointCount = n;
            // C# 侧置信度硬编码 0.9f，此处同样置 0.9 以对齐 IsValid 判定
            for (int i = 0; i < 21 && i < n; ++i) h.scores[i] = 0.9f;
            if (n < 21) for (int i = n; i < 21; ++i) h.scores[i] = 0.0f;
        }

        q = obj.find("\"box\":[");
        if (q != std::string::npos) {
            double b[4];
            if (std::sscanf(obj.c_str() + q + 6, "%lf,%lf,%lf,%lf", &b[0], &b[1], &b[2], &b[3]) == 4) {
                h.box.x = static_cast<float>(b[0]);
                h.box.y = static_cast<float>(b[1]);
                h.box.w = static_cast<float>(b[2] - b[0]);
                h.box.h = static_cast<float>(b[3] - b[1]);
            }
        }

        // keypoints:[[x,y],...]
        q = obj.find("\"keypoints\":[");
        if (q != std::string::npos) {
            const char* s = obj.c_str() + q + 12;
            int idx = 0;
            while (idx < 21 && *s) {
                while (*s == '[' || *s == ',' || *s == ' ') ++s;
                if (*s != '[') break;
                ++s;
                char* end = nullptr;
                double x = std::strtod(s, &end);
                if (end == s) break;
                s = end;
                while (*s == ',' || *s == ' ') ++s;
                double y = std::strtod(s, &end);
                if (end == s) break;
                s = end;
                if (*s == ']') ++s;
                h.points[idx].x = static_cast<float>(x);
                h.points[idx].y = static_cast<float>(y);
                ++idx;
            }
        }

        // ★ 语义槽位：把 21 点按 MediaPipe 标准序号填入
        //   （RK3588 侧若换模型，这里改为查 kSlotToModelKpt 映射表）
        h.slot[static_cast<int>(HandSlot::kWrist)]      = h.points[0];
        h.slot[static_cast<int>(HandSlot::kThumbTip)]   = h.points[4];
        h.slot[static_cast<int>(HandSlot::kIndexTip)]   = h.points[8];
        h.slot[static_cast<int>(HandSlot::kMiddleTip)]  = h.points[12];
        h.slot[static_cast<int>(HandSlot::kRingTip)]    = h.points[16];
        h.slot[static_cast<int>(HandSlot::kPinkyTip)]   = h.points[20];
        h.slot[static_cast<int>(HandSlot::kIndexMcp)]   = h.points[5];
        h.slot[static_cast<int>(HandSlot::kThumbIp)]    = h.points[3];
        h.slot[static_cast<int>(HandSlot::kPinkyMcp)]   = h.points[17];
        // kPalmCenter：5 指 MCP 均值（当前 C# 判定链路未使用，此处备齐）
        Point palm{0, 0};
        const int mcpIdx[5] = {2, 5, 9, 13, 17};
        for (int i : mcpIdx) { palm.x += h.points[i].x; palm.y += h.points[i].y; }
        h.slot[static_cast<int>(HandSlot::kPalmCenter)] = Point{palm.x / 5.0f, palm.y / 5.0f};
        for (int i = 0; i < kHandSlotCount; ++i) h.slotValid[i] = h.validPointCount > 0;

        fd.hands.push_back(h);

        p = e + 1;
        if (p < line.size() && line[p] == ',') { p++; continue; }
        break;
    }
    return fd;
}

int parseIntField(const std::string& line, const char* key, int def) {
    std::string pat = std::string("\"") + key + "\":";
    size_t p = line.find(pat);
    if (p == std::string::npos) return def;
    return std::atoi(line.c_str() + p + pat.size());
}

std::string parseStrField(const std::string& line, const char* key) {
    std::string pat = std::string("\"") + key + "\":\"";
    size_t p = line.find(pat);
    if (p == std::string::npos) return "";
    p += pat.size();
    size_t e = line.find('"', p);
    return line.substr(p, e - p);
}

std::string jsonEscape(const std::string& s) {
    std::string o;
    for (char c : s) {
        if (c == '"' || c == '\\') { o += '\\'; o += c; }
        else o += c;
    }
    return o;
}

}  // namespace

// =============================================================================
int main(int argc, char** argv) {
    if (argc < 3) {
        std::cerr << "用法: replay_main <inputs.jsonl> <out_decisions.jsonl> [recipe.yaml] [start_step_id]\n";
        return 2;
    }

    const std::string inPath  = argv[1];
    const std::string outPath = argv[2];
    const std::string recipe  = (argc >= 4) ? argv[3] : "";

    // 起始步骤 id（可选，第 4 个参数）
    // ★ 为什么需要：黄金录制窗口是中途打开的，录制首帧时 Windows 状态机
    //   已经在某个中间步骤（如喝水配方停在 step 2），而 C++ 从配方第一步起步。
    //   不指定就会造成"步骤错位"，比对结果全是假差异。
    //   用法：replay_main in.jsonl out.jsonl recipe.yaml 2
    int startStepId = 0;
    if (argc >= 5) startStepId = std::atoi(argv[4]);

    // ---- 加载配方 ----
    SOPWorkflow workflow;
    std::vector<ZoneDefinition> zones;
    if (!recipe.empty()) {
        if (!loadRecipe(recipe, workflow, zones)) {
            std::cerr << "配方加载失败: " << recipe << "\n";
            return 3;
        }
        std::cerr << "[replay] 配方已加载: " << recipe
                  << "  步骤数=" << workflow.steps.size()
                  << "  区域数=" << zones.size() << "\n";
    } else {
        std::cerr << "[replay] 未指定配方，仅跑引擎流程（无法评估条件）\n";
    }

    SOPStateMachine sm(&zones);
    if (!recipe.empty()) {
        if (startStepId > 0) sm.StartAt(workflow, 0, startStepId);
        else              sm.Start(workflow, 0);
    }

    // ---- 逐帧回放 ----
    std::ifstream fin(inPath);
    if (!fin) { std::cerr << "打不开: " << inPath << "\n"; return 4; }
    std::ofstream fout(outPath);
    if (!fout) { std::cerr << "写不了: " << outPath << "\n"; return 5; }

    std::string line;
    int n = 0;
    while (std::getline(fin, line)) {
        if (line.empty()) continue;

        int frame = parseIntField(line, "frame", n + 1);
        int ts = parseIntField(line, "ts_ms", n * 33);
        int w = parseIntField(line, "w", 1280);
        int h = parseIntField(line, "h", 720);
        n++;

        std::vector<Detection> dets = parseObjects(line);
        FrameDetections hands = parseHands(line, w, h);
        hands.frameIndex = frame;
        hands.timestampMs = ts;

        if (!recipe.empty()) sm.ProcessFrame(dets, &hands, ts);

        // ---- 输出 decisions（字段名/格式与 Windows 版一致）----
        const ConditionEvaluationResult* ev = sm.lastEvaluation();
        const SOPStep* step = sm.currentStep();

        fout << "{\"frame\":" << frame
             << ",\"ts_ms\":" << ts
             << ",\"step_id\":" << sm.currentStepId()
             << ",\"step_name\":\"" << jsonEscape(step ? step->stepName : "") << "\""
             << ",\"state\":\"" << toString(sm.currentState()) << "\""
             << ",\"is_pass\":" << (ev && ev->isPass ? "true" : "false")
             << ",\"next_step\":";
        {
            int nx = -1;
            if (step && !step->nextSteps.empty()) {
                try { nx = std::stoi(step->nextSteps[0]); } catch (...) {}
            }
            fout << nx;
        }
        fout << ",\"conditions\":{";
        if (step && ev) {
            bool first = true;
            // ★ 与 Windows 版一致：遍历 PassConditions，同序取 CheckResults；
            //   AND 短路时未评估的条件记 false
            for (size_t i = 0; i < step->passConditions.size(); ++i) {
                if (!first) fout << ",";
                first = false;
                bool met = (i < ev->checkResults.size()) && ev->checkResults[i].isMet;
                fout << "\"" << toString(step->passConditions[i].type) << "\":"
                     << (met ? "true" : "false");
            }
        }
        fout << "},\"violations\":[";
        {
            bool first = true;
            for (const auto& v : sm.violations()) {
                // 只记录本帧附近触发的（对齐 Windows 版 200ms 窗口）
                if (ts - v.timestampMs >= 0 && ts - v.timestampMs < 200) {
                    if (!first) fout << ",";
                    first = false;
                    fout << "\"" << toString(v.type) << "\"";
                }
            }
        }
        // violations 数组 "]" 收尾，然后是本行的 "}"
        fout << "]}\n";
    }

    std::cerr << "[replay] 回放完成: " << n << " 帧 → " << outPath << "\n";
    return 0;
}