namespace LifeDrop.Mobile;

// Taking a customer's order (owner request 2026-11-23: separate from the product gallery). The home button opens the customer choice, then the
// order page for that customer (warehouse, quantities, place).
public static class OrderEntry
{
    public static Task StartAsync(INavigation navigation) =>
        navigation.PushAsync(new CustomerPickPage(async customer => await navigation.PushAsync(new OrderPage(customer.Id, customer.Name))));
}
