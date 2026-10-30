// 原规格 20 §5 的六个错误类 —— 对应 js/errors.js。
export declare class CapabilityDeniedError extends Error {
  readonly capability: string;
  readonly capabilityId: string;
  constructor(capabilityId: string, detail?: string);
}

export declare class WrongContextError extends Error {
  readonly expectedContext: string;
  readonly actualContext: string;
  constructor(expected: string, actual: string);
}

export declare class BudgetExceededError extends Error {
  readonly budget: 'cpu' | 'memory' | 'handles' | 'network';
  constructor(budget: 'cpu' | 'memory' | 'handles' | 'network', detail?: string);
}

export declare class ManifestError extends Error {
  readonly manifestPath: string;
  readonly errors: readonly string[];
  constructor(manifestPath: string, errors?: readonly string[]);
}

export declare class DisposedError extends Error {
  constructor(what?: string);
}

export declare class ExperimentalRequiredError extends Error {
  readonly feature: string;
  constructor(feature: string, pluginId?: string);
}
