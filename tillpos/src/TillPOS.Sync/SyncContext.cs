using TillPOS.Data;
using TillPOS.Erp;

namespace TillPOS.Sync;

public sealed record SyncContext(IErpClient Erp, CatalogStore Store, KeysetPager Pager, string PosProfile);
