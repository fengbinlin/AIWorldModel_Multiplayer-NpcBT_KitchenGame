namespace Kitchen
{
    public class ClearCounter : BaseCounter
    {
        public override void Interact(ICanHoldKitchenObj holder)
        {
            //尝试进行盘子的操作
            if (CounterOperator.TryPlateOperator(holder, this)) return;

            //玩家持有物体，当前柜子没有物体
            if (holder.HasKitchenObj() && !HasKitchenObj())
            {

                KitchenObjOperator.PutKitchenObj(holder, this);
                return;
            }

            //玩家没有持有物体，当前柜子有物体
            if (!holder.HasKitchenObj() && HasKitchenObj())
            {
                KitchenObjOperator.PutKitchenObj(this, holder);
                return;
            }
        }
    }
}