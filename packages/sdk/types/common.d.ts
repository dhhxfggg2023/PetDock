// PetDock SDK 类型绑定 —— 原规格 20 模块面的 TypeScript 声明。
//
// 为什么手写而不从 JSDoc 生成：原规格 20 的模块面是**契约**，不是实现产物。生成器会把
// 占位实现的形状（比如 tick 的 `world: unknown`）原样搬进声明，而契约里 world 是有
// 明确字段的（原规格 11 §2）。手写声明才能让「文档说的」和「编辑器补全的」是同一件事。
//
// 版本对齐：原规格 20 §4 的 api 版本 = 1.0。ABI 1.x 内只增不改。

/** 原规格 20 §1 模块面的 7 个入口。 */
export type SdkModule =
  | '@petdock/sdk'
  | '@petdock/sdk/presenter'
  | '@petdock/sdk/brain'
  | '@petdock/sdk/interaction'
  | '@petdock/sdk/state'
  | '@petdock/sdk/capabilities'
  | '@petdock/sdk/log';

/** 原规格 20 §4：宿主与 SDK 的版本协商结果。 */
export interface RuntimeApi {
  /** SDK 的 api major.minor。 */
  readonly api: { readonly major: number; readonly minor: number };
  /** 宿主报告的平台标识，如 'win32-x64'。 */
  readonly platform: string;
  /** 宿主版本串，如 '1.0.0'。 */
  readonly host: string;
  /** 原规格 20 §6 的稳定等级。 */
  readonly stability: 'stable' | 'experimental' | 'deprecated';
}

/** 原规格 11 §3.1 PetDescription —— presenter 向宿主自述的能力。 */
export interface PetDescription {
  /** 展示名。 */
  readonly name: string;
  /** 帧尺寸（像素）。 */
  readonly frame: { readonly width: number; readonly height: number };
  /** 动画表：名字 → 帧序列。 */
  readonly animations: Readonly<Record<string, readonly number[]>>;
  /** 默认动画名，必须是 animations 的键。 */
  readonly defaultAnimation: string;
}

/** 原规格 11 §2.1 Frame —— presenter 产出的单帧。 */
export interface Frame {
  /** 位图句柄或 BGRA 缓冲区。 */
  readonly content: unknown;
  /** 相对宠物窗口左上角的锚点。 */
  readonly anchor: { readonly x: number; readonly y: number };
  /** 不透明度 0..1。 */
  readonly opacity: number;
}

/** 原规格 11 §2.4 Event —— 宿主投递给插件的事件包。 */
export interface PetEvent {
  readonly kind: string;
  readonly payload?: unknown;
  /** 单调递增的序号，用于插件丢弃乱序事件。 */
  readonly seq: number;
}

/** 原规格 11 §4 BrainDirective —— brain 对宿主的指令。 */
export interface BrainDirective {
  readonly kind: string;
  readonly payload?: unknown;
}

/** 原规格 11 §2 的 world 快照，tick 的第二个参数。 */
export interface WorldSnapshot {
  /** 距上一 tick 的毫秒数。 */
  readonly dt: number;
  /** 光标在宠物窗口坐标系里的位置；窗口外为 null。 */
  readonly cursor: { readonly x: number; readonly y: number } | null;
  /** 当前动画名。 */
  readonly animation: string;
  /** 单调递增的 tick 序号。 */
  readonly tick: number;
}

/** 原规格 11 §3 Presenter 接口。 */
export interface Presenter {
  meta(): { readonly name: string; readonly version: string };
  init(ctx: unknown): Promise<void>;
  describe(): PetDescription;
  attach(): Promise<void>;
  detach(): Promise<void>;
  dispose(): Promise<void>;
}

/** 原规格 11 §4 Brain 接口。 */
export interface Brain {
  meta(): { readonly name: string; readonly version: string };
  init(ctx: unknown): Promise<void>;
  tick(dt: number, world: WorldSnapshot): BrainDirective[];
  onEvent(event: PetEvent): void;
  dispose(): Promise<void>;
}

/** 原规格 11 §5 InteractionSurface 接口。 */
export interface InteractionSurface {
  readonly id: string;
  readonly priority: number;
  hitTest(x: number, y: number): boolean;
  onPointerDown(x: number, y: number): unknown;
}

/** 原规格 11 §6 PetState 接口。 */
export interface PetState {
  readonly schemaVersion: number;
  readAll(): unknown;
  read(key: string): unknown;
  write(key: string, value: unknown): void;
  commit(): void;
  rollback(): void;
  migrate(from: number, to: number): void;
}

/** 原规格 20 §2 的能力标识；`string` 允许插件自定义能力名。 */
export type CapabilityId =
  | 'input.mousePosition'
  | 'persist.write:self'
  | (string & {});
