using System;
using System.Collections.Generic;
using RouterPilot.Models;

namespace RouterPilot.Services;

public interface IDataFreshnessService
{
    event Action? Changed;
    void Configure(string source, TimeSpan expectedRefreshInterval);
    void MarkAttempt(string source);
    void MarkSuccess(string source);
    void MarkUnavailable(string source);
    void Refresh();
    DataFreshnessInfo Get(string source);
    IReadOnlyList<DataFreshnessInfo> GetAll();
    void BeginReestablishmentWindow(TimeSpan duration);
}
