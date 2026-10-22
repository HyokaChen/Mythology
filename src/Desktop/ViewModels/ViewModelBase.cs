using ReactiveUI;

namespace Desktop.ViewModels;

/// <summary>所有 ViewModel 的基类（ReactiveObject，提供 RaiseAndSetIfChanged 等）。</summary>
public abstract class ViewModelBase : ReactiveObject;
