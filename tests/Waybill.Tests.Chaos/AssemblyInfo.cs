// Concurrency, process kill and Toxiproxy scenarios (stage 3 onward). This project runs as its own CI job;
// tests marked [Trait("Category", "Long")] (1 h broker outage, 24 h load, 8 dispatchers for 10 min) run only
// in the scheduled workflow.
[assembly: CaptureConsole]
