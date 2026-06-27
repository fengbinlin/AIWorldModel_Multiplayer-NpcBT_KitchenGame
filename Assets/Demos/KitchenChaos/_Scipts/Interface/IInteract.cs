namespace Kitchen
{
    public interface IInteract
    {
        void Interact(ICanHoldKitchenObj holder);
    }
    public interface IInteractAlternate
    {
        void InteractAlternate(ICanHoldKitchenObj holder);
    }
}