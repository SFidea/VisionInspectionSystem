// =============================================================================
//  yaml_config.h  —  SOP 配方解析（yaml-cpp）
// =============================================================================
//  移植自 C# SOPYamlConfig.cs 的解析 + ToWorkflow 转换层。
//
//  ★ 两条铁律：
//  1. 配方【原样兼容】—— configs/sop/*.yaml 一个字节都不改。
//  2. 解析器必须【容忍未知字段】—— 对应 C# 的 .IgnoreUnmatchedProperties()。
//     YamlDotNet 默认遇未知属性抛异常，曾导致 5 个旧配方全部加载失败（2026-09-27）。
// =============================================================================
#pragma once

#include <string>
#include <vector>

#include "sop/types.h"

namespace vis {
namespace sop {

/// 加载配方：解析 YAML → SOPWorkflow + 区域列表
/// @return 成功返回 true
bool loadRecipe(const std::string& yamlPath,
                SOPWorkflow& outWorkflow,
                std::vector<ZoneDefinition>& outZones);

}  // namespace sop
}  // namespace vis