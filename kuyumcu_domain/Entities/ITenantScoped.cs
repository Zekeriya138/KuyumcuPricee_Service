using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace kuyumcu_domain.Entities
{
    // TenantId taşıyan tüm tablolar için ortak arayüz
    public interface ITenantScoped
    {
        Guid TenantId { get; set; }
    }
}
