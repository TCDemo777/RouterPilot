using System;
using System.Collections.Generic;
using RouterPilot.Models;

namespace RouterPilot.Services;

public interface IClientPresenceHistoryService
{
    void Observe(IEnumerable<ClientInfo> clients);
    IReadOnlyList<ClientPresencePeriod> GetRecent(string normalizedMac, DateTimeOffset from, DateTimeOffset to);
    TimeSpan GetObservedOnlineToday(string normalizedMac, DateTimeOffset now);
    IReadOnlyList<ClientDailyAvailability> GetDailyAvailability(string normalizedMac, int days, DateTimeOffset now);
    ClientPresencePeriod? GetCurrentPeriod(string normalizedMac);
    bool Clear(string normalizedMac);
    void CloseSession();
}
