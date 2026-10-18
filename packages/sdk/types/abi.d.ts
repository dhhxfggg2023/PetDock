// 四个 ABI 接口的声明入口 —— 对应 js/{presenter,brain,interaction,state}.js。
// docs/11 是这些形状的权威来源；本文件只做「文档 → 编辑器」的搬运，不发明字段。
export type {
  Brain,
  BrainDirective,
  Frame,
  InteractionSurface,
  PetDescription,
  PetEvent,
  PetState,
  Presenter,
  WorldSnapshot,
} from './common.js';
