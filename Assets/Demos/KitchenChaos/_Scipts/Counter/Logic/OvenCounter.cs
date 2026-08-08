namespace Kitchen
{
    /// <summary>Oven: unbaked pizza → baked pizza.</summary>
    public class OvenCounter : TimedFacilityCounter
    {
        protected override FacilityEnum ProcessFacility => FacilityEnum.OvenCounter;
    }
}
