# FantasyBeastStories — 技术架构文档

## 目录

1. [项目概述](#1-项目概述)
2. [程序集分层架构](#2-程序集分层架构)
3. [Core 层 — 纯数据与契约](#3-core-层--纯数据与契约)
4. [Framework 层 — 基础设施与服务](#4-framework-层--基础设施与服务)
5. [Modules 层 — 13 个功能模块](#5-modules-层--13-个功能模块)
6. [事件驱动系统](#6-事件驱动系统)
7. [网络同步架构](#7-网络同步架构)
8. [XLua 热更新集成](#8-xlua-热更新集成)
9. [对象池系统](#9-对象池系统)
10. [存档系统](#10-存档系统)
11. [启动引导流程](#11-启动引导流程)
12. [MVC 模式实践](#12-mvc-模式实践)
13. [卡牌系统](#13-卡牌系统)
14. [符文系统](#14-符文系统)
15. [设计决策与问题解决](#15-设计决策与问题解决)

---

## 1. 项目概述

FantasyBeastStories 是一款基于 Unity 2022.3 的多人在线对战游戏，支持实时网络对战、角色养成、卡牌选择、符文装备等核心玩法。

### 技术栈

| 领域 | 技术方案 |
|---|---|
| 网络框架 | Photon PUN2 |
| 热更新 | XLua + Addressables |
| 补间动画 | DOTween |
| 渲染管线 | URP |
| 相机系统 | Cinemachine |
| 文本渲染 | TextMeshPro |
| 序列化 | Newtonsoft.Json |

### 代码规模

- ~250 个 C# 脚本
- 13 个功能模块
- 5 个自定义程序集（asmdef）
- 8 个 Lua 脚本

---

## 2. 程序集分层架构

### 依赖图

```
┌──────────────────────────────────────────────────────┐
│                 FantasyBeastStories.Editor            │
│            (Editor Only, 工具与代码生成配置)            │
└────────────────────┬─────────────────────────────────┘
                     │
┌────────────────────┴─────────────────────────────────┐
│                FantasyBeastStories.Modules            │
│         (13 个功能模块: 战斗/角色/卡牌/网络/...)         │
└──────┬──────────┬──────────────┬─────────────────────┘
       │          │              │
       ▼          ▼              ▼
┌──────────┐ ┌──────────────┐ ┌──────┐
│ XLua     │ │  Framework   │ │ 第三方 │
│ (热更新)  │ │ (基础设施)    │ │ 插件   │
└────┬─────┘ └──────┬───────┘ └──────┘
     │               │
     └───────┬───────┘
             ▼
┌──────────────────────┐
│ FantasyBeastStories   │
│       Core            │
│  (纯 C# 数据与契约)    │
└──────────────────────┘
```

### 程序集引用关系

| 程序集 | 引用 |
|---|---|
| **Core** | 无（零依赖） |
| **Framework** | Core, Photon, Addressables, DOTween |
| **Modules** | Core, Framework, XLua, Photon, TMP, Addressables, DOTween, Cinemachine, URP |
| **Editor** | Core, Framework, Modules, XLua |
| **XLua** | Core, Framework |

### 设计决策：为什么选择三层分离

**问题**：项目最初所有代码在 `Assembly-CSharp` 中编译，存在循环依赖（Core 层 Model 引用 Module 层 Controller 类型，Framework 层 EventChannel 引用 Module 层数据类型）。

**解决**：
1. 将所有被 Core 和 Framework 依赖的类型（枚举、数据类、接口、SO 定义）移到 `Core/SharedModel/` 目录
2. Framework 通过接口（`INetworkPlayerService` 等）解耦 Module 实现
3. 使用 Bootstrap 模式让各 Module 自行注册工厂方法，InfrastructureRegistrar 不直接引用 Module 类型

---

## 3. Core 层 — 纯数据与契约

Core 层是整个项目的基石，**零外部依赖**，所有类型都是纯 C# 或仅依赖 `UnityEngine`。

### 目录结构

```
Core/
├── EventArgsBase.cs           — 事件参数基类（含时间戳）
├── Contracts/
│   └── ISpawnPoint.cs         — 生成点接口
└── SharedModel/
    ├── SharedEnums.cs         — Element, EnemyState, EnemyReportType, CardQuality, CardScope
    ├── PlayerModel.cs         — 玩家模型（HP/伤害/元素/恢复）
    ├── EnemyModel.cs          — 敌人模型（状态机/伤害/重置）
    ├── GameModel.cs           — 游戏模型（战斗状态/生成点）
    ├── AttributePlayerBase.cs — 玩家属性（攻击/防御/暴击/元素/分裂）
    ├── AttributeEnemyBase.cs  — 敌人属性（生命/攻击/速度）
    ├── CardConfigSO.cs        — 卡牌配置 SO
    ├── ICardEffect.cs         — 卡牌效果接口 + 上下文接口
    ├── CardEffects.cs         — 14 种卡牌效果实现
    ├── MagicUpgradeModel.cs   — 卡牌选择模型
    ├── TaskBase.cs / KillTask.cs / EscortTask.cs — 任务数据类
    ├── GameTimeModel.cs       — 游戏时间模型
    ├── NetworkTarget.cs       — 网络 RPC 目标枚举
    ├── SkillQueryData.cs      — 技能查询数据
    ├── LuaDelegates.cs        — C#→Lua 委托类型定义
    └── ...
```

### 设计决策：为什么把共享类型放在 Core

**问题**：asmdef 拆分前，`EnemyModel` 引用 `Controllers.Character.AttributeEnemyBase`，`TaskModel` 引用 `Controllers.Task.TaskBase`。这些都是 Core 层 Model 引用 Module 层 Controller 类型，违反分层架构。

**解决**：将 `AttributeEnemyBase`、`TaskBase` 等被多个 Module 共享的数据类型移到 `Core/SharedModel/`，统一命名空间为 `Core.SharedModel`。这样 Core 不再依赖任何 Module，依赖方向变为单向。

---

## 4. Framework 层 — 基础设施与服务

Framework 层提供游戏运行的通用基础设施，不包含具体游戏逻辑。

### 子系统一览

| 子系统 | 职责 | 关键类 |
|---|---|---|
| **Bootstrap** | 启动注册 | `InfrastructureRegistrar`, `ServiceLocator`, `GameServiceRegistrar` |
| **EventSystem** | 事件驱动 | `BaseEventChannelSO`, `EventChannelContainerSO`, `EventChannelLocator` |
| **Service/Network** | 网络抽象 | `NetworkServiceLocator`, `PhotonCallbackBridge` |
| **Service/Pool** | 对象池 | `ObjectPoolManager`, `PoolHelper`, `PoolConst` |
| **Service/Save** | 存档 | `SaveManager`, `ISaveable`, `FileDataHandler` |
| **Service/Asset** | 资源加载 | `AssetLoader`, `AddressablesUpdater` |
| **Service/Audio** | 音频 | `AudioManager`, `AudioMixerController`, `SoundLibrarySO` |
| **UI** | UI 框架 | `UIManager`, `UIScreen`, `UIWidget`, `UINavigationStack` |

### 契约层（Contracts）

Framework 定义了所有网络相关的接口，Module 层实现它们：

```csharp
// Framework/Common/Contracts/
INetworkPlayerService  — 玩家身份、属性、房间事件
INetworkObjectService  — 网络对象实例化、RPC、ViewID
IObjectPoolService     — 对象池操作
IGameActionService     — 游戏动作（暂停、返回大厅）
IControllerRpcService  — Domain 层 RPC 调用
```

### 设计决策：为什么用接口而不是直接依赖

**问题**：`PhotonCallbackBridge`（Framework）需要转发 PUN 回调给 `PhotonPlayerService`（Module），但 Framework 不能引用 Module。

**解决**：在 `INetworkPlayerService` 接口中定义 `NotifyPropertyChanged` 等方法，`PhotonCallbackBridge` 通过接口调用，不依赖具体类型。`Module_NetworkBootstrap` 在启动时将具体实现注册到 `NetworkServiceLocator`。

---

## 5. Modules 层 — 13 个功能模块

| 模块 | 职责 | 文件数 |
|---|---|---|
| **Module_Battle** | 战斗系统：敌人 AI、投射物、HUD、匹配统计 | ~67 |
| **Module_Network** | Photon 网络服务、RPC Bridge、投射物同步 | ~16 |
| **Module_Lobby** | 大厅 UI、场景管理、角色选择 | ~23 |
| **Module_Character** | 玩家控制器、Lua 桥接、输入系统 | ~18 |
| **Module_Rune** | 符文装备、效果应用、UI 动画 | ~16 |
| **Module_Task** | 任务系统、任务 UI | ~16 |
| **Module_Card** | 卡牌数据库 | ~9 |
| **Module_Experience** | 经验/升级系统 | ~9 |
| **Module_Game** | GameManager、CoinManager、LuaEnvManager | ~3 |
| **Module_Shop** | 商店系统 | ~5 |
| **Module_Time** | 时间循环、敌人属性缩放 | ~3 |
| **Module_RedDot** | 红点通知系统 | ~3 |
| **Module_Minimap** | 小地图渲染 | ~4 |

### 设计决策：为什么从 Module_Lobby 拆分出 Module_Network 和 Module_Game

**问题**：Module_Lobby 承载了 5 种职责——网络服务、RPC Bridge、大厅 UI、游戏管理、相机控制。42 个文件混合在一起，职责不清晰。

**解决**：
1. 将 16 个网络相关文件（PhotonPlayerService、PhotonObjectService、RPC Bridges、CastNetwork 等）拆到 `Module_Network`
2. 将 3 个游戏管理文件（GameManager、CoinManager、LuaEnvManager）拆到 `Module_Game`
3. Module_Lobby 瘦身到 23 个文件，只保留大厅 UI 和场景管理

---

## 6. 事件驱动系统

### 架构

事件系统采用 **ScriptableObject 事件通道** 模式，分三层：

```
Layer 1: BaseEventChannelSO<T>          (Framework/Common/EventSystem/Base/)
  ├── 泛型基类，支持 0/1/2 个参数
  ├── C# event (Action<T>) 内部委托
  ├── Raise() — 遍历监听器，try/catch 隔离异常
  └── RegisterListener() / UnregisterListener()

Layer 2: 具体事件通道 SO (Combat/, Game/, Player/, UI/)
  ├── DamageEventChannelSO : BaseEventChannelSO<DamageEventArgs>
  ├── SkillQueryEventChannelSO : BaseEventChannelSO<SkillQueryData>
  ├── TimeEventChannelSO : BaseEventChannelSO<TimeEventArgs>
  └── ... 30+ 种事件通道

Layer 3: EventChannelContainerSO (主容器)
  ├── 引用 4 个子容器: CombatChannelsSO, PlayerChannelsSO, GameChannelsSO, UIChannelsSO
  ├── 通过 Resources.Load 在 BeforeSceneLoad 阶段加载
  └── 通过 EventChannelLocator.MainContainer 静态访问
```

### 设计决策：为什么用 ScriptableObject 而不是 C# event

**优势**：
- **解耦**：发送方和接收方只需持有同一个 SO 引用，不需要互相知道
- **编辑器可视化**：Inspector 中可查看事件通道的监听器
- **数据驱动**：事件通道是资产，可以热更新

---

## 7. 网络同步架构

### 分层设计

```
┌─────────────────────────────────────────────────┐
│              CastNetwork (Module_Network)        │
│    投射物同步、伤害广播、特效同步                    │
│    [PunRPC] RPC_OnFireballCast, RPC_DealDamage  │
├─────────────────────────────────────────────────┤
│   RPC Bridges (Module_Network/Controller/Bridge)│
│    ManagerRpcBridge   — Application 层 RPC       │
│    ControllerRpcBridge — Domain 层 RPC           │
│    UIRpcBridge        — Presentation 层 RPC      │
├─────────────────────────────────────────────────┤
│   PhotonCallbackBridge (Framework)               │
│    转发 PUN 回调 → INetworkPlayerService          │
│    ViewID 分配                                   │
├─────────────────────────────────────────────────┤
│   NetworkServiceLocator (Framework)              │
│    静态服务定位器: PlayerService, ObjectService    │
├─────────────────────────────────────────────────┤
│   Contracts (Framework/Common/Contracts/)        │
│    INetworkPlayerService, INetworkObjectService   │
│    IObjectPoolService, IGameActionService          │
├─────────────────────────────────────────────────┤
│   Photon PUN2 (第三方)                            │
│    PhotonNetwork, PhotonView, MonoBehaviourPun     │
└─────────────────────────────────────────────────┘
```

### 网络同步流程示例：投射物发射

```
玩家攻击 → AttackRangeBase.PerformAttack()
  │
  ├─ 本地: ComponentFactory.GetOrCreateImpactCannon() → 发射投射物
  │
  └─ 网络: CastNetwork.BroadcastFireball()
            │
            └─ photonView.RPC("RPC_OnFireballCast", NetworkTarget.Others, ...)
                  │
                  └─ 远程客户端: RPC_OnFireballCast()
                        ├─ ComponentFactory.GetOrCreateImpactCannon() → 远程投射物
                        └─ _isMine = false → 不计算伤害，只显示特效
```

### 设计决策：为什么用三层 RPC Bridge

**问题**：最初所有 `[PunRPC]` 方法散布在各个 Manager 和 Controller 中，网络代码与业务逻辑耦合。

**解决**：创建三个 RPC Bridge，按架构层分离：
- `ManagerRpcBridge` — 统管 GameManager、CoinManager 等的 RPC
- `ControllerRpcBridge` — 统管战斗控制器的 RPC  
- `UIRpcBridge` — 统管 UI 同步的 RPC

每个 Bridge 是一个 `DontDestroyOnLoad` 的 GameObject + `PhotonView`，在 `Module_NetworkBootstrap.AfterSceneLoad` 时创建。

---

## 8. XLua 热更新集成

### 架构

```
┌──────────────────────────────────────────────┐
│              Lua 脚本 (Addressables)            │
│  Heroes/WizardBoy.lua  Combat/BingNvAttack.lua │
│  Logic/RuneEffect.lua  Logic/RuneSpecialPower  │
├──────────────────────────────────────────────┤
│           FantasyBeastLuaConfig (Editor)        │
│  [LuaCallCSharp] — 18 个类型生成静态 Wrap        │
│  [CSharpCallLua] — 7 个委托类型生成适配代码       │
│  [GCOptimize]    — 3 个值类型优化 GC             │
├──────────────────────────────────────────────┤
│            LuaDelegates (Core)                   │
│  LuaVoidAction, LuaSkillQueryAction, ...        │
├──────────────────────────────────────────────┤
│         HeroLuaBridge / AttackLuaBridge          │
│  委托缓存 (零 GC) + 异常隔离 + C# 回退            │
├──────────────────────────────────────────────┤
│           LuaEnvManager (Module_Game)           │
│  Addressables Loader + require() 缓存            │
├──────────────────────────────────────────────┤
│              XLua Runtime                        │
└──────────────────────────────────────────────┘
```

### 委托缓存模式

```csharp
// 旧方式（每次调用都创建+销毁 LuaFunction，有 GC）
fn = _behavior.Get<LuaFunction>("OnStart");
fn.Call(player);
fn.Dispose();

// 新方式（委托缓存，零 GC）
_onStart = _behavior.Get<LuaVoidAction>("OnStart"); // 只缓存一次
_onStart(player); // 直接调用，无 GC
```

### 设计决策：为什么用委托缓存替代 LuaFunction.Call

**问题**：原始实现中每次调用 Lua 函数都通过 `LuaTable.Get<LuaFunction>()` 获取函数对象，调用后 `Dispose()`。每次调用产生一次 GC 分配。

**解决**：在 XLua 配置中声明 `[CSharpCallLua]` 委托类型，使用 `LuaTable.Get<DelegateType>()` 一次性缓存委托，后续直接调用。委托类型定义在 `Core/SharedModel/LuaDelegates.cs` 中（所有程序集可访问）。

### Lua 加载方式

```
LuaEnvManager.CustomLoader:
  filePath "Heroes.WizardBoy" → "Heroes_WizardBoy"
  Addressables.Load("Lobby_Lua_Heroes_WizardBoy") → TextAsset.bytes
  LuaEnv.DoString("return require('Heroes.WizardBoy')") → LuaTable
```

---

## 9. 对象池系统

### ObjectPoolManager

- **Queue 复用**：`Queue<GameObject> available` — O(1) 获取/归还
- **HashSet 成员检查**：`HashSet<GameObject> allObjectsSet` — O(1) 判断对象是否属于该池
- **Rigidbody 缓存**：`Dictionary<GameObject, Rigidbody>` — 避免重复 `GetComponent`
- **动态扩容**：池为空时按需实例化新对象
- **网络感知**：等待网络连接后才初始化池

### PoolHelper（零 GC 热路径）

```csharp
// 绕过事件通道，直接调用 ObjectPoolManager
private static ObjectPoolManager _mgr;
public static GameObject Get(string poolName, Vector3? pos = null)
{
    if (_mgr == null)
        _mgr = ServiceLocator.Get<ObjectPoolManager>();
    return _mgr.GetFromPool(poolName, pos);
}
```

### 设计决策：为什么有 PoolHelper 绕过事件通道

**问题**：对象池操作（Get/Return）是高频调用（每帧多次投射物）。通过 EventChannelSO 走事件系统会产生 lambda 闭包的 GC 分配。

**解决**：`PoolHelper` 是静态类，直接通过 `ServiceLocator` 获取 `ObjectPoolManager` 引用并缓存，调用路径无 GC。

---

## 10. 存档系统

### 架构

```
SaveManager (Singleton + DontDestroyOnLoad)
  │
  ├── ISaveable 注册表
  │   ├── SaveManager 自身 (生涯经验/角色选择)
  │   ├── ShopManager (购买记录)
  │   ├── RuneInventory (符文装备)
  │   ├── TaskManager (任务进度)
  │   └── ... 任何实现 ISaveable 的系统自动参与
  │
  ├── 存档流程
  │   CollectDataFromManagers() → ISaveable.OnSave(SaveData)
  │   → Newtonsoft.Json 序列化 → FileDataHandler 写文件
  │
  ├── 读档流程
  │   FileDataHandler 读文件 → Newtonsoft 反序列化
  │   → HandleVersionCompatibility() → ValidateSaveData()
  │   → DistributeDataToManagers() → ISaveable.OnLoad(SaveData)
  │
  └── SaveData (POCO)
      版本号、时间戳、金币、经验、卡牌、符文、任务、商店、设置
```

### ISaveable 接口

```csharp
public interface ISaveable
{
    string SaveId { get; }
    void OnSave(SaveData data);
    void OnLoad(SaveData data);
}
```

### 设计决策：为什么用 ISaveable 插件模式

**优势**：新增系统只需实现 `ISaveable` 并调用 `SaveManager.RegisterSaveable(this)`，不需要修改 SaveManager 代码。开闭原则。

---

## 11. 启动引导流程

### 初始化时序

```
BeforeSceneLoad 阶段:
  ┌─ InfrastructureRegistrar.RegisterFactories()
  │   ├─ PhotonNetwork.SendRate = 30
  │   ├─ Resources.Load("EventChannels/MainEventChannels") → ServiceLocator
  │   ├─ GameServiceRegistrar.EnsureRegistered() → 注册早期服务 stub
  │   └─ PhotonCallbackBridge.EnsureExists() → DontDestroyOnLoad
  │
  └─ Module_NetworkBootstrap.RegisterNetworkServices()
      └─ NetworkServiceLocator.Register(PhotonPlayerService, PhotonObjectService)

AfterSceneLoad 阶段:
  ┌─ Module_NetworkBootstrap.RegisterFactories()
  │   ├─ ComponentFactory.RegisterNetworkCasterCreator(CastNetwork)
  │   ├─ 创建 ManagerRpcBridge (DontDestroyOnLoad + PhotonView)
  │   ├─ 创建 ControllerRpcBridge (DontDestroyOnLoad + PhotonView)
  │   ├─ 创建 UIRpcBridge (DontDestroyOnLoad + PhotonView)
  │   └─ PhotonCallbackBridge.RegisterBridgeView(pv) → 等待 OnJoinedRoom 分配 ViewID
  │
  └─ Module_BattleBootstrap.RegisterFactories()
      └─ ComponentFactory.RegisterImpactCannonCreator(ImpactCannon)

运行时:
  ├─ ObjectPoolManager.Start() → 等待网络连接 → InitializePool()
  ├─ SaveManager.Start() → 自动读档
  ├─ Launcher.Awake() → 设置 GameServiceRegistrar 回调
  └─ LuaEnvManager → 懒加载（首次访问时初始化）
```

### 设计决策：为什么用 Bootstrap 模式

**问题**：`InfrastructureRegistrar`（Framework）最初直接引用 `ImpactCannon`、`CastNetwork`、`ManagerRpcBridge` 等 Module 类型来注册工厂方法，造成 Framework→Module 的反向依赖。

**解决**：每个 Module 创建自己的 Bootstrap 类，通过 `[RuntimeInitializeOnLoadMethod]` 自行注册。`InfrastructureRegistrar` 只负责 Core/Framework 级别的注册，不再引用任何 Module 类型。

---

## 12. MVC 模式实践

### Player MVC

```
┌─────────────────────────────────────────────┐
│              PlayerController (View+Controller) │
│  MonoBehaviour, 持有 PlayerModel               │
│  处理: Animator, Rigidbody, RPC, 事件通道      │
│  实现: ICardEffectContext                      │
├─────────────────────────────────────────────┤
│              PlayerModel (Model)               │
│  纯 C# 类，不继承 MonoBehaviour                  │
│  聚合: AttributePlayerBase + PlayerMovementData │
│  方法: CalculateFinalDamage(), ApplyDamage()    │
│        → 返回 PlayerDamageResult (struct)       │
│        TickHealthRecover(), 元素管理             │
├─────────────────────────────────────────────┤
│         AttributePlayerBase (数据)              │
│  攻击力/防御力/暴击/元素/分裂/连击等属性           │
│  带百分比的 Add 方法 (AddAttackPower 等)         │
└─────────────────────────────────────────────┘
```

### 设计决策：为什么 Model 返回 struct 而不是 void

**问题**：如果 Model 直接调用 Controller 的方法（如刷新 UI、发 RPC），Model 就依赖了 Controller，破坏了可测试性。

**解决**：Model 的方法返回结果 struct（`PlayerDamageResult`、`DamageResult`），Controller 根据返回值决定外部联动。Model 完全不知道 UI 和网络的存在。

---

## 13. 卡牌系统

### 数据驱动设计

```
CardConfigSO (ScriptableObject)
  ├── 基础信息: cardId, cardName, description, value
  ├── 分类: quality (Normal/Epic/Legend), scope (Public/Exclusive)
  ├── 绑定: characterType (专属卡绑定的角色)
  └── Effects: [SerializeReference] List<ICardEffect>
      ├── AddAttackPowerEffect     — 增加攻击力
      ├── AddMaxHealthEffect       — 增加最大生命
      ├── AddCriticalChanceEffect  — 增加暴击率
      ├── SwitchElementEffect      — 切换元素
      ├── SetSplitEffect           — 启用分裂
      └── ... 14 种效果
```

### 卡牌选择流程

```
MagicUpgradeManager.OpenPanel()
  │
  ├─ MagicUpgradeModel.ResolveCardSelection()
  │   ├─ 获取 3 张卡 (普通或专属)
  │   ├─ 幸运值判定: luckRate * 0.8% 概率替换一张为专属卡
  │   └─ 返回 CardConfigSO[]
  │
  └─ 玩家选择 → OnApplicationCard(card)
      └─ 遍历 card.Effects → effect.Apply(this)
          └─ 通过 ICardEffectContext 接口调用 PlayerController 能力
```

### 设计决策：为什么用 [SerializeReference] + 接口

**优势**：
- **策略模式**：每种效果是一个 `[Serializable]` 类，可在 Inspector 中配置数值
- **开闭原则**：新增卡牌效果只需新建一个类实现 `ICardEffect`，不修改现有代码
- **数据驱动**：卡牌效果完全由 `.asset` 文件定义，可热更新

### 踩坑与解决

**问题**：asmdef 拆分后，`CardConfigSO` 在 `FantasyBeastStories.Core` 程序集中，但卡牌效果实现 `CardEffects.cs` 在 `FantasyBeastStories.Modules` 程序集中。`.asset` 文件中 `[SerializeReference]` 存储的类型信息指向旧的 `Assembly-CSharp`，导致反序列化失败。

**解决**：
1. 将 `CardEffects.cs` 移到 `Core/SharedModel/`（与 `CardConfigSO` 和 `ICardEffect` 同程序集）
2. 批量更新 38 个 `.asset` 文件中的类型信息：`ns: Controllers.CardData, asm: Assembly-CSharp` → `ns: Core.SharedModel, asm: FantasyBeastStories.Core`

---

## 14. 符文系统

### 架构

```
RuneEquipmentSnapshot (静态缓存)
  │  装备前: 从 RunePanel UI 捕获装备的符文 ID
  │  战斗中: GetAllEquippedIds() 返回装备列表
  │
  ▼
RuneEffectApplier (静态应用器)
  │
  ├─ 加载 RuneDatabase (Addressables 热更)
  │
  ├─ 遍历每个装备的符文
  │   ├─ ApplyPower(attr, power)
  │   │   ├─ Lua 映射: label → methodName (RuneEffect.lua)
  │   │   ├─ 委托字典: methodName → Action<AttributePlayerBase, float>
  │   │   └─ C# 回退: switch(power.label)
  │   │
  │   └─ ApplySpecialPower(attr, runeData)
  │       ├─ Lua 函数: RuneSpecialPower[name] → luaFunc.Call(attr)
  │       └─ C# 回退: switch(specialPowerName)
  │
  └─ 输出最终属性日志
```

### 委托字典（替代反射）

```csharp
private static readonly Dictionary<string, Action<AttributePlayerBase, float>> _effectActions = new()
{
    { "AddAttackPower",       (attr, v) => attr.AddAttackPower(v) },
    { "AddCriticalChance",    (attr, v) => attr.AddCriticalChance(v) },
    { "AddDefensePower",      (attr, v) => attr.AddDefensePower(v) },
    { "ReduceAttackInterval", (attr, v) => attr.ReduceAttackInterval((int)v) },
    // ...
};

// 查找: O(1) 字典查找，零反射开销
if (_effectActions.TryGetValue(methodName, out var action))
    action(attr, power.value);
```

### 设计决策：为什么用委托字典替代反射

**问题**：原始实现使用 `typeof(AttributePlayerBase).GetMethod(methodName).Invoke()` 反射调用，有性能开销和类型安全风险。

**解决**：用 `Dictionary<string, Action<AttributePlayerBase, float>>` 预构建方法映射。Lua 返回方法名字符串后，查字典直接调用委托。O(1) 查找，零反射，类型安全。

---

## 15. 设计决策与问题解决

### 15.1 asmdef 拆分中的循环依赖

**问题**：
- Core 层 `EnemyModel` 引用 Module 层 `AttributeEnemyBase`
- Framework 层 `EventChannelSO` 引用 Module 层 `CardConfigSO`
- `Controllers.Network` 命名空间在 Framework 和 Module 中同时存在

**解决**：
1. 将所有共享类型移到 `Core/SharedModel/`
2. Framework 通过接口解耦 Module 实现
3. 统一命名空间：`Controllers.Combat` + `Controllers.Enemy` → `Controllers.Battle`，`Controllers.CardData` → `Controllers.Card`
4. 消除 `Managers` 命名空间，改为模块专属命名空间

### 15.2 SerializeReference 跨程序集序列化断裂

**问题**：`[SerializeReference]` 在 `.asset` 文件中存储 `AssemblyQualifiedName`。asmdef 拆分后类型从 `Assembly-CSharp` 移到 `FantasyBeastStories.Core`，旧 `.asset` 文件反序列化失败。

**解决**：批量替换 38 个 `.asset` 文件中的 `ns` 和 `asm` 字段。

### 15.3 Lua 命名空间引用失效

**问题**：asmdef 拆分后，`Element` 枚举从全局命名空间移到 `Core.SharedModel`，`SkillQueryType` 从 `Core.Channels.Player` 移到 `Core.SharedModel`。但 Lua 代码中 `CS.Core.Channels.Player.SkillQueryType.GetMaxAttackCount` 引用旧命名空间，返回 nil，导致条件判断永远不匹配。

**解决**：更新所有 Lua 文件中的 `CS.` 引用：
- `CS.Element` → `CS.Core.SharedModel.Element`
- `CS.Core.Channels.Player.SkillQueryType` → `CS.Core.SharedModel.SkillQueryType`
- `CS.Controllers.Network.NetworkTarget` → `CS.Core.SharedModel.NetworkTarget`
- `CS.Controllers.Services.NetworkServiceLocator` → `CS.Core.Network.NetworkServiceLocator`

### 15.4 ImpactCannon 穿透效果失效

**问题**：`ImpactCannon.OnEnable()` 在 `attributePlayer` 被设置之前就尝试读取 `GetSplit()`，由于 `attributePlayer` 为 null，`_canSplit` 被设为 `false`。之后 `SetAttributeFromPlayer()` 虽然设置了 `attributePlayer`，但没有重新读取 split 数据。

**解决**：在 `SetAttributeFromPlayer()` 中添加 split 数据的读取。

### 15.5 GameServiceRegistrar 与 Launcher 的直接依赖

**问题**：`GameServiceRegistrar`（Framework）中的 `EarlyObjectPoolService` 直接调用 `Launcher.Instance.ReturnToLobby()`，造成 Framework→Module 依赖。

**解决**：改为回调委托模式。`GameServiceRegistrar` 暴露 `Action ReturnToLobbyCallback` 等属性，`Launcher.Awake()` 中设置回调。

### 15.6 XLua 代码生成与 asmdef 循环依赖

**问题**：XLua asmdef 不能引用 Modules（因为 Modules 引用 XLua，会循环依赖），但 XLua 自动发现的类型包含 Modules 中的 `GamePauseManager`、`GameManager` 等，生成的 Wrap 代码无法编译。

**解决**：XLua asmdef 只引用 Core 和 Framework。`[LuaCallCSharp]` 配置中只声明 Core/Framework 类型。Modules 中的类型在运行时通过 XLua 反射访问（默认行为）。每次执行 `XLua > Generate Code` 后，手动删除 `Gen/Controllers_*Wrap.cs` 和 `UI_*Wrap.cs`，并清理 `XLuaGenAutoRegister.cs` 中的对应引用。

---

## 附录：架构模式总览

| 模式 | 应用位置 |
|---|---|
| 分层架构 | Core → Framework → Modules → Editor |
| 依赖倒置 | Framework 定义接口，Module 实现 |
| 服务定位器 | ServiceLocator + NetworkServiceLocator |
| 事件驱动 | ScriptableObject EventChannelSO |
| MVC/MVP | 纯 C# Model + MonoBehaviour Controller + Unity View |
| 策略模式 | ICardEffect 14 种实现 |
| 工厂方法 | ComponentFactory (ImpactCannon, CastNetwork) |
| Bootstrap 模式 | 各 Module 自行注册工厂方法 |
| 对象池 | Queue + HashSet + Rigidbody 缓存 |
| 委托缓存 | HeroLuaBridge/AttackLuaBridge |
| 插件模式 | ISaveable 存档注册 |
| RPC Bridge | 三层 PhotonView 分离 |
