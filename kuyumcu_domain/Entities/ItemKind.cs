using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace kuyumcu_domain.Entities
{
   
    public enum ItemKind
    {
        Unknown = 0,
        Ziynet = 1,        // Tam/Yarım/Çeyrek/Gram vb.
        CraftedGold = 2,   // Bilezik, yüzük, kolye...
        GramGold = 3,      // Has gram satışları
        Silver = 4,        // Gümüş
        Forex = 5,         // Döviz
        Other = 9,
        Finished=6,
        Product =7
    }
}
