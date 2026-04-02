using System;

namespace ESAPI_EQD2Viewer.UI.ViewModels
{
    /// <summary>
    /// Lightweight event bus for decoupled communication between child ViewModels.
    ///
    /// Replaces direct cross-references between partial classes with a publish/subscribe
    /// model. Each child ViewModel publishes domain events; others subscribe to what
    /// they need. The bus is owned by MainViewModel and injected into each child.
    ///
    /// Events are intentionally simple — no async, no queuing. All handlers run
    /// synchronously on the UI thread since WPF rendering requires it anyway.
 /// </summary>
    internal sealed class ViewModelEventBus
    {
        // ?? Rendering triggers ??????????????????????????????????????????

    /// <summary>Raised when any state change requires a full scene re-render.</summary>
        public event Action RenderRequested;

        /// <summary>Request a scene re-render from any child ViewModel.</summary>
   public void RequestRender() => RenderRequested?.Invoke();

        // ?? EQD2 / ?/? changes ?????????????????????????????????????????

        /// <summary>Raised when the display ?/? slider value changes.</summary>
        public event Action<double> DisplayAlphaBetaChanged;

        public void OnDisplayAlphaBetaChanged(double alphaBeta)
        => DisplayAlphaBetaChanged?.Invoke(alphaBeta);

        /// <summary>Raised when the EQD2 enabled toggle changes.</summary>
        public event Action<bool> EQD2EnabledChanged;

      public void OnEQD2EnabledChanged(bool enabled)
  => EQD2EnabledChanged?.Invoke(enabled);

        /// <summary>Raised when number of fractions changes.</summary>
        public event Action<int> FractionsChanged;

        public void OnFractionsChanged(int fractions)
=> FractionsChanged?.Invoke(fractions);

        // ?? DVH recalculation ???????????????????????????????????????????

        /// <summary>Raised when DVH curves need full recalculation (e.g. ?/? or fx changed).</summary>
   public event Action DVHRecalculationRequested;

        public void RequestDVHRecalculation() => DVHRecalculationRequested?.Invoke();

        // ?? Summation lifecycle ?????????????????????????????????????????

        /// <summary>Raised when summation completes or is cleared.</summary>
        public event Action<SummationStateChangedArgs> SummationStateChanged;

        public void OnSummationStateChanged(SummationStateChangedArgs args)
     => SummationStateChanged?.Invoke(args);
    }

    /// <summary>
    /// Payload for the SummationStateChanged event.
    /// </summary>
    internal sealed class SummationStateChangedArgs
    {
  public bool IsActive { get; set; }
   public double MaxDoseGy { get; set; }
        public double ReferenceDoseGy { get; set; }
     public string StatusMessage { get; set; }
    }
}
