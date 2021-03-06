﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Thrift;
using Thrift.Protocol;
using Thrift.Transport;

namespace ThriftTest {
  class Program {
    static void Main(string[] args) {
      try {
        var port = 9095;
        TTransport transport = new TSocket("localhost", port);
        TProtocol protocol = new TBinaryProtocol(transport);
        ThriftTestService.Client client = new ThriftTestService.Client(protocol);
        Console.WriteLine("ThriftTest opening transport on port {0}", port);
        transport.Open();
        try {
          RunTests(client);
        } finally {
          transport.Close();
        }
      } catch (Exception x) {
        //} catch (TApplicationException x) {
        Console.WriteLine(x.ToString());
      }

    }
    static void RunTests(ThriftTestService.Client client) {
      RunTest("AddVR4");
      client.AddVR4(new List<VR4> {
        new VR4 { },
        new VR4 { AB = true, AD = DateTime.Today.Ticks, AN = 12346.02, AS = "A R4 string"} 
      });
      ///
      RunTest("GetVB", client.GetVB());
      RunTest("GetVD", client.GetVD().ToTime());
      RunTest("GetVI", client.GetVI());
      RunTest("GetVN", client.GetVN().ToNumber());
      RunTest("GetVS", client.GetVS());
      RunTest("GetVU", client.GetVU());
      RunTest("GetVT4", client.GetVT4());
      RunTest("GetVT5", client.GetVT5());
      RunTest("GetVR4", client.GetVR4());
      RunTest("GetVR5", client.GetVR5());
      RunTest("GetVConcat", client.GetVConcat());

      RunTest("GetFB", client.GetFB(true));
      RunTest("GetFD", client.GetFD(DateTime.Today.Ticks).ToTime());
      RunTest("GetFI", client.GetFI(12345));
      RunTest("GetFN", client.GetFN(1234.5678).ToNumber());
      RunTest("GetFS", client.GetFS("Another string"));
      RunTest("GetFU", client.GetFU(new ut4 { AB = true, AD = DateTime.Today.Ticks, AN = 1234.5679, AS = "A UT string"} ));
      RunTest("GetFT4", client.GetFT4(new VT4 { AB = true, AD = DateTime.Today.Ticks, AI = 12346, AS = "A T4 string"}));
      RunTest("GetFT5", client.GetFT5(new VT5 { AB = true, AD = DateTime.Today.Ticks, AI = 12346, AN = DateTime.Today.Ticks, AS = "A T5 string"}));
      RunTest("GetFR4", client.GetFR4(new List<VR4> {
        new VR4 { AB = true, AD = DateTime.Today.Ticks, AN = 12346.01, AS = "A R4 string"},
        new VR4 { AB = true, AD = DateTime.Today.Ticks, AN = 12346.02, AS = "A R4 string"} 
      }));
      RunTest("GetFR5", client.GetFR5(new List<VT5> {
        new VT5 { AB = true, AD = DateTime.Today.Ticks, AI = 12367, AN = 12346.01, AS = "A R4 string"},
        new VT5 { AB = true, AD = DateTime.Today.Ticks, AI = 12368, AN = 12346.02, AS = "A R4 string"} 
      }));
      RunTest("GetFConcat", client.GetFConcat(true, DateTime.Today.Ticks, 12357, 12345.6789, "A concat string"));

      // updates
      RunTest("AddVR4");
      client.AddVR4(new List<VR4> {
        new VR4 { AB = true, AD = DateTime.Today.Ticks, AN = 12346.01, AS = "A R4 string"},
        new VR4 { AB = true, AD = DateTime.Today.Ticks, AN = 12346.02, AS = "A R4 string"} 
      });
      RunTest("AddVR4");
      client.AddVR4(new List<VR4> {
        new VR4 { },
        new VR4 { AB = true, AD = DateTime.Today.Ticks, AN = 12346.02, AS = "A R4 string"} 
      });

      // errors
      try {
        client.DoErrorA();
      } catch (Exception ex) {
        Console.WriteLine("Test {0} exception {1}", "DoErrorA", ex.Message);
      }
      try {
        client.DoErrorB();
      } catch (Exception ex) {
        Console.WriteLine("Test {0} exception {1}", "DoErrorB", ex.Message);
      }
    }

    static void RunTest(string name) {
      Console.WriteLine("Test {0}", name);
    }

    static void RunTest(string name, object value) {
      Console.WriteLine("Test {0} value {1}", name, value);
    }

    static void RunTest(string name, IEnumerable<object> values) {
      Console.WriteLine("Test {0}", name);
      foreach (var value in values)
        Console.WriteLine("  {0}", value);
    }

  }

  public static class ThriftTypeExtensions {
    public static DateTime ToTime(this long arg) {
      return new DateTime(arg);
    }
    public static Decimal ToNumber(this double arg) {
      return (Decimal)arg;
    }
  }
}
