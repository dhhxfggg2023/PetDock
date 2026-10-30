// 自检判据的回调形状（2026-10-28）：原先寄居在被删掉的 WinForms 外观/授权检查里，
// 这些委托本身与页面实现无关，独立成文件后，剩下的非 UI 判据仍能共用。
namespace PetDock.Hosting;

internal delegate void Report(string name, bool ok, string detail);

internal delegate void ConsentCheck(string name, bool ok, string detail);

internal delegate void LineSink(string line);
