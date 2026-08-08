namespace Kitchen
{
    /// <summary>Blender: salad (unmixed shake) → milkshake.</summary>
    public class BlenderCounter : TimedFacilityCounter
    {
        protected override FacilityEnum ProcessFacility => FacilityEnum.BlenderCounter;
    }
}
