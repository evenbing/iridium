using System;

namespace Iridium.DB.Test
{
    public interface IOrder
    {
        Customer Customer { get; set; }
    }

    public class Order : IOrder
    {
        public Order()
        {
            OrderDate = DateTime.Now;
        }

        public int OrderID { get; set; }
        public int CustomerID { get; set; }

        [Column.ForeignKey(typeof(SalesPerson))]
        public int? SalesPersonID { get;set; }

        [Column.Name("Date")]
        public DateTime OrderDate { get; set; }

        public string Remark { get; set; }

        [Relation(LocalKey = "SalesPersonID")]
        public SalesPerson SalesPerson { get; set; }
        
        public Customer Customer { get; set; }

        [Relation]
        public IDataSet<OrderItem> OrderItems { get; set; }
    }
}