// 旧 UI 宿主对新契约（PetDock.App）的实现半边（R1 起）。
//
// 这里只做**翻译**：旧方法的失败形式是「null = 成功，非 null = 一句给人看的话」，
// 契约统一成 AppResult。等 R8 删掉旧层时，这个文件与 UiHost*.cs 一起搬走——
// 那时才真正把实现从旧 UI 里挪出来，现在先把调用面固定住。
using System;
using PetDock.App;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost : IPetsService
{
    AppResult IPetsService.SetOnDesktop(string petId, bool onDesktop) =>
        Translate(SetPetOnDesktopFromPanel(petId, onDesktop));

    AppResult IPetsService.Duplicate(string petId) => Translate(DuplicatePetFromPanel(petId));

    AppResult IPetsService.Delete(string petId) => Translate(DeletePetFromPanel(petId));

    AppResult IPetsService.SetSize(string? petId, int size) => Translate(SetPetSizeFromPanel(petId, size));

    /// <summary>旧层的失败形式是 <c>string?</c>：null = 成功，非 null = 原因。</summary>
    private static AppResult Translate(string? failure) =>
        failure is null ? AppResult.Success() : AppResult.Failure(failure);
}
