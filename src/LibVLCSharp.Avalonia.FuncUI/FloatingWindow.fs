namespace LibVLCSharp.Avalonia.FuncUI

open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Interactivity
open Avalonia.Media
open Avalonia.Platform
open Avalonia.Win32
open Avalonia.Layout
open Avalonia.VisualTree

open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.FuncUI.Builder

open System

open NativeModule

type FloatingWindowImpl() =
    inherit WindowImpl()

    static let ownerList = MailboxProcessor.createAgent Map.empty

    let tryGetOwner (x: FloatingWindowImpl) =
        Map.tryFind x.Handle.Handle
        |> MailboxProcessor.postAndReply ownerList

    let (|OwnerHandle|_|) (x: FloatingWindowImpl) =
        tryGetOwner x |> Option.map WindowBase.getHandle

    static member Register (floatingWindow: WindowBase) owner =
        Map.add floatingWindow.PlatformImpl.Handle.Handle owner
        |> MailboxProcessor.post ownerList

    override x.WndProc(hWnd, msg, wParam, lParam) =
        match msg, wParam, x with
        | WM_NCACTIVATE, Active, OwnerHandle owner ->
            PostMessage(owner, msg, nativeBool true, 0)
            |> ignore

            ``base``.WndProc(hWnd, msg, wParam, lParam)

        | WM_NCACTIVATE, Deactive, OwnerHandle (NotEq lParam owner) ->

            PostMessage(owner, msg, wParam, lParam) |> ignore

            ``base``.WndProc(hWnd, msg, wParam, lParam)
        | _ -> ``base``.WndProc(hWnd, msg, wParam, lParam)

module FloatingWindowImpl =
    let tryGet () =
        if Environment.OSVersion.Platform = PlatformID.Win32NT then
            new FloatingWindowImpl() :> IWindowImpl |> Some
        else
            None


type FloatingWindow() =
    inherit WindowWrapper
        (
            FloatingWindowImpl.tryGet (),
            SystemDecorations = SystemDecorations.None,
            TransparencyLevelHint = WindowTransparencyLevel.Transparent,
            Background = Brushes.Transparent,
            TransparencyBackgroundFallback = Brushes.Black,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false
        )

    let visualLayerManagerSub = Subject.behavior None

    let getVisualRoot (visual: IVisual) = visual.VisualRoot :?> WindowBase

    member val Owner = Option<IVisual>.None with get, set

    member x.VisualLayerManager =
        match visualLayerManagerSub.Value with
        | Some _ as vm -> vm
        | None ->
            x.GetVisualDescendants()
            |> Seq.tryPick (function
                | :? VisualLayerManager as m ->
                    Some m |> visualLayerManagerSub.OnNext
                    visualLayerManagerSub.Value
                | _ -> None)

    member x.VisualLayerManagerObservable: IObservable<_> = visualLayerManagerSub

    member x.RaizeOwnerEvent e =
        match x.Owner with
        | Some (:? IInteractive as i) -> i.RaiseEvent e
        | _ -> ()

    override x.OnInitialized() =
        let callback e =
            match x.Content with
            | :? IControl as c when not c.IsPointerOver && x.IsPointerOver -> x.RaizeOwnerEvent e
            | _ -> ()

        x.PointerPressed |> Observable.add callback

        x.PointerReleased |> Observable.add callback

        x.GetPropertyChangedObservable WindowBase.ContentProperty
        |> Observable.add (fun e ->
            match e.NewValue with
            | :? IView as v -> x.Content <- VirtualDom.VirtualDom.create v
            | _ -> ())

#if DEBUG
        x.AttachDevTools()
#endif

type FloatingWindowOwnerImpl() =
    inherit WindowImpl()

    let getOwnerHandle (f: FloatingWindow) =
        match f.Owner with
        | Some o ->
            o.VisualRoot :?> WindowBase
            |> WindowBase.getHandle
        | None -> IntPtr.Zero

    let isToClientFloating (window: WindowBase) handle (ownerImpl: WindowImpl) =
        match window with
        | :? FloatingWindow as f ->
            WindowBase.getHandle f = handle
            && getOwnerHandle f = ownerImpl.Handle.Handle
        | _ -> false

    let (|ToClientFloating|_|) (ownerImpl: WindowImpl, handle) =
        getCurrentWindows ()
        |> Seq.tryPick (function
            | f when isToClientFloating f handle ownerImpl -> Some ToClientFloating
            | _ -> None)

    override x.WndProc(hWnd, msg, wParam, lParam) =

        match msg, wParam, (x, lParam) with
        | WM_NCACTIVATE, Deactive, ToClientFloating -> ``base``.WndProc(hWnd, msg, nativeBool true, 0)
        | _ -> ``base``.WndProc(hWnd, msg, wParam, lParam)

module FloatingWindowOwnerImpl =
    let tryGet () =
        if Environment.OSVersion.Platform = PlatformID.Win32NT then
            new FloatingWindowOwnerImpl() :> IWindowImpl
            |> Some
        else
            None

type FloatingOwnerHost() as x =
    inherit ContentControl()

    let hostDisposables = CompositeDisposable.create ()
    let floatingDisposables = CompositeDisposable.create ()

    let floatingWindowSub = FloatingWindow(Owner = Some x) |> Subject.behavior
    let isAttachedSub = Subject.behavior false

    let initNewSizeToContent =
        function
        | (HorizontalAlignment.Stretch, VerticalAlignment.Stretch) -> SizeToContent.Manual
        | (HorizontalAlignment.Stretch, _) -> SizeToContent.Width
        | (_, VerticalAlignment.Stretch) -> SizeToContent.Height
        | (_, _) -> SizeToContent.Manual

    let initGetNewWidth (horizontalAlignment: HorizontalAlignment) =
        if horizontalAlignment = HorizontalAlignment.Stretch then
            fun (host: FloatingOwnerHost) -> host.Bounds.Width
        else
            fun _ -> Double.NaN

    let initGetNewHeight (verticalAlignment: VerticalAlignment) =
        if verticalAlignment = VerticalAlignment.Stretch then
            fun (host: FloatingOwnerHost) -> host.Bounds.Height
        else
            fun _ -> Double.NaN

    let initGetLeft =
        function
        | HorizontalAlignment.Right ->
            fun (host: FloatingOwnerHost) (manager: VisualLayerManager) -> host.Bounds.Width - manager.Bounds.Width
        | HorizontalAlignment.Center -> fun host manager -> (host.Bounds.Width - manager.Bounds.Width) / 2.0
        | _ -> fun _ _ -> 0.0

    let initGetTop =
        function
        | VerticalAlignment.Bottom ->
            fun (host: FloatingOwnerHost) (manager: VisualLayerManager) -> host.Bounds.Height - manager.Bounds.Height
        | VerticalAlignment.Center -> fun host manager -> (host.Bounds.Height - manager.Bounds.Height) / 2.0
        | _ -> fun _ _ -> 0.0

    let mutable newSizeToContent = SizeToContent.Manual
    let mutable getNewWidth = fun _ -> Double.NaN
    let mutable getNewHeight = fun _ -> Double.NaN
    let mutable getNewLeft = fun _ _ -> Double.NaN
    let mutable getNewTop = fun _ _ -> Double.NaN

    member inline private x.UpdateFloatingCore(manager: VisualLayerManager) =
        manager.MaxWidth <- x.Bounds.Width
        manager.MaxHeight <- x.Bounds.Height

        floatingWindowSub.Value.SizeToContent <- newSizeToContent
        floatingWindowSub.Value.Width <- getNewWidth x
        floatingWindowSub.Value.Height <- getNewHeight x

        let newPosition =
            Point(getNewTop x manager, getNewLeft x manager)
            |> x.PointToScreen

        if newPosition <> floatingWindowSub.Value.Position then
            floatingWindowSub.Value.Position <- newPosition

    member inline private x.UpdateFloating() =
        match floatingWindowSub.Value.VisualLayerManager with
        | Some manager -> x.UpdateFloatingCore manager
        | None -> ()

    member inline private x.InitFloatingWindow(floatingWindow: FloatingWindow) =
        floatingWindow[!FloatingWindow.ContentProperty] <- x[!FloatingOwnerHost.ContentProperty]

        floatingWindow.VisualLayerManagerObservable
        |> Observable.subscribe (function
            | Some vm ->
                vm.GetObservable VisualLayerManager.HorizontalAlignmentProperty
                |> Observable.combineLatest2 (vm.GetObservable VisualLayerManager.VerticalAlignmentProperty)
                |> Observable.subscribe (fun (v, h) ->
                    newSizeToContent <- initNewSizeToContent (h, v)
                    getNewWidth <- initGetNewWidth h
                    getNewHeight <- initGetNewHeight v
                    getNewLeft <- initGetLeft h
                    getNewTop <- initGetTop v)
                |> floatingDisposables.Add
            | _ -> ())
        |> floatingDisposables.Add

        let root = x.GetVisualRoot() :?> WindowBase

        Observable.ignore root.PositionChanged
        |> Observable.mergeIgnore (root.GetObservable Window.WindowStateProperty)
        |> Observable.mergeIgnore (x.GetObservable FloatingOwnerHost.ContentProperty)
        |> Observable.mergeIgnore (x.GetObservable FloatingOwnerHost.BoundsProperty)
        |> Observable.subscribe (fun _ ->
            match floatingWindow.VisualLayerManager with
            | Some manager -> x.UpdateFloatingCore manager
            | None -> ())
        |> floatingDisposables.Add

    override x.OnInitialized() =
        base.OnInitialized()

        x.GetObservable ContentControl.IsVisibleProperty
        |> Observable.combineLatest3 floatingWindowSub isAttachedSub
        |> Observable.subscribe (fun (floating, isAttached, isVisible) ->

            if floating.IsVisible && isAttached = isVisible then
                ()
            elif isVisible && isAttached then
                x.InitFloatingWindow floatingWindowSub.Value

                task {
                    do! Task.delayMilliseconds 1
                    x.UpdateFloating()
                }
                |> ignore

                x.GetVisualRoot() :?> Window |> floating.Show

            else
                floatingDisposables.Clear()
                floating.Hide())
        |> hostDisposables.Add

    override x.OnAttachedToVisualTree e =
        isAttachedSub.OnNext true
        base.OnAttachedToVisualTree e

    override x.OnDetachedFromVisualTree e =
        isAttachedSub.OnNext false
        base.OnDetachedFromVisualTree e

    member x.FloatingWindow
        with get () = floatingWindowSub.Value
        and set (value: FloatingWindow) =
            if not <| refEquals floatingWindowSub.Value value then
                floatingDisposables.Clear()

                floatingWindowSub.Value.Close()
                floatingWindowSub.Value.Owner <- None
                value.Content <- floatingWindowSub.Value.Content
                value.Owner <- Some x
                floatingWindowSub.OnNext value

    static member FloatingWindowProperty =
        AvaloniaProperty.RegisterDirect<FloatingOwnerHost, _>(
            nameof
                Unchecked.defaultof<FloatingOwnerHost>
                    .FloatingWindow,
            (fun o -> o.FloatingWindow),
            (fun o v -> o.FloatingWindow <- v)
        )

module FloatingOwnerHost =
    let create (attrs) =
        ViewBuilder.Create<FloatingOwnerHost> attrs

    let floatingWindow<'t when 't :> FloatingOwnerHost> (floatingWindow: FloatingWindow) : IAttr<'t> =
        AttrBuilder<'t>
            .CreateProperty<FloatingWindow>(FloatingOwnerHost.FloatingWindowProperty, floatingWindow, ValueNone)