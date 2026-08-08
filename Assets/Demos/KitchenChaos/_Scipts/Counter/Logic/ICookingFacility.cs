using System;



namespace Kitchen

{

    /// <summary>

    /// Shared cooking events for stove / oven / blender visuals and audio.

    /// </summary>

    public interface ICookingFacility

    {

        event Action OnStartCooking;

        event Action OnStopCooking;

        event Action<KitchenObjEnum?> OnCookingStageChange;

    }

}


