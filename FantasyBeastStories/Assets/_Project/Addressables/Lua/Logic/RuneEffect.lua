-- RuneEffect.lua
-- 符文效果映射表：label → C# 的 AttributePlayerBase 方法名
-- 热更新时可新增效果类型，无需改 C# 的 switch-case

local RuneEffect = {
    -- label（与 RunePower.label 对应）→ 要调用的 C# 方法名
    ["%基础伤害"]   = "AddAttackPower",
    ["%暴击率"]     = "AddCriticalChance",
    ["%防御力"]     = "AddDefensePower",
    ["%攻击速度"]   = "ReduceAttackInterval",

    -- 热更新可新增的效果类型 
    -- ["%移动速度"] = "AddMoveSpeed",
    -- ["%生命偷取"] = "AddLifeSteal",
    -- ["%生命恢复"] = "AddHealthRecover",
}

return RuneEffect