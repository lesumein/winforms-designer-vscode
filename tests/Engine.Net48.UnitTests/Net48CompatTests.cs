using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using WinFormsDesigner.Engine;
using WinFormsDesigner.Engine.Net48;
using Xunit;

namespace Engine.Net48.UnitTests
{
    /// <summary>The .NET Framework half of the designer-compatibility shapes: MSChart locals actually executing against
    /// the framework DataVisualization assembly, and the z-ordered capture that replaces Control.DrawToBitmap.</summary>
    public sealed class Net48CompatTests
    {
        private sealed class TestHost : IIrHost
        {
            private static readonly Assembly[] Probe =
            {
                typeof(Control).Assembly, typeof(Color).Assembly, typeof(Font).Assembly, typeof(Point).Assembly,
                typeof(ISupportInitialize).Assembly, typeof(object).Assembly, typeof(Chart).Assembly,
            };
            public Type ResolveType(string typeName)
            {
                foreach (var a in Probe) { var t = a.GetType(typeName, false); if (t != null) return t; }
                return Type.GetType(typeName, false);
            }
            public object CreateComponent(Type type, string name, bool withContainer) => Activator.CreateInstance(type);
            public object ResolveResource(string key, bool isString) => null;
            public bool WasResourceRefused(string key) => false;
            public bool ApplyResources(object target, string key, out string error) { error = "no test resources"; return false; }
        }

        [Fact]
        public void ChartLocals_Execute_AgainstTheFrameworkChartAssembly()
        {
            const string src = @"
namespace Demo {
  partial class Form1 {
    private System.Windows.Forms.DataVisualization.Charting.Chart chart1;
    private void InitializeComponent() {
      System.Windows.Forms.DataVisualization.Charting.ChartArea chartArea1 = new System.Windows.Forms.DataVisualization.Charting.ChartArea();
      System.Windows.Forms.DataVisualization.Charting.Series series1 = new System.Windows.Forms.DataVisualization.Charting.Series();
      this.chart1 = new System.Windows.Forms.DataVisualization.Charting.Chart();
      ((System.ComponentModel.ISupportInitialize)(this.chart1)).BeginInit();
      chartArea1.AxisX.Maximum = 1000D;
      chartArea1.AxisX.Minimum = 10D;
      chartArea1.Name = ""ChartArea1"";
      this.chart1.ChartAreas.Add(chartArea1);
      series1.ChartArea = ""ChartArea1"";
      series1.ChartType = System.Windows.Forms.DataVisualization.Charting.SeriesChartType.Line;
      series1.Name = ""Series1"";
      this.chart1.Series.Add(series1);
      this.Controls.Add(this.chart1);
      ((System.ComponentModel.ISupportInitialize)(this.chart1)).EndInit();
    }
  }
}";
            var doc = DesignerIrBuilder.Build(src);
            Assert.True(doc.FullCoverage, string.Join(" | ", doc.UnrepresentableReasons));
            var res = DesignerIrExecutor.Execute(doc, new Form(), new TestHost());
            Assert.True(res.Ok, res.FailureReason);

            var chart = (Chart)res.Instances["chart1"];
            var area = chart.ChartAreas.Single();
            Assert.Equal("ChartArea1", area.Name);
            Assert.Equal(1000D, area.AxisX.Maximum);
            Assert.Equal(10D, area.AxisX.Minimum);
            var series = chart.Series.Single();
            Assert.Equal(SeriesChartType.Line, series.ChartType);
            Assert.Equal("ChartArea1", series.ChartArea);
        }

        [Fact]
        public void ZOrderedCapture_PaintsTheFrontControlOverAnOverlappingSibling()
        {
            using (var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
                Location = new Point(-20000, -20000), ClientSize = new Size(200, 100), ShowInTaskbar = false,
            })
            {
                var back = new Panel { Bounds = new Rectangle(0, 0, 200, 100), BackColor = Color.Red };
                var front = new Panel { Bounds = new Rectangle(20, 20, 60, 40), BackColor = Color.Blue };
                form.Controls.Add(back);
                form.Controls.Add(front);
                form.Controls.SetChildIndex(front, 0); // front: index 0 = top of the z-order (a fresh designer drop)
                form.Show();
                Application.DoEvents();

                using (var bmp = new Bitmap(form.Width, form.Height))
                {
                    ZOrderedCapture.DrawToBitmap(form, bmp, new Rectangle(0, 0, form.Width, form.Height));
                    Assert.Equal(Color.Blue.ToArgb(), bmp.GetPixel(50, 40).ToArgb()); // inside the front panel
                    Assert.Equal(Color.Red.ToArgb(), bmp.GetPixel(150, 80).ToArgb()); // back panel only
                }
            }
        }

        [Fact]
        public void ZOrderedCapture_ClipsAChildToItsParentsClientArea()
        {
            using (var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
                Location = new Point(-20000, -20000), ClientSize = new Size(200, 100), ShowInTaskbar = false,
                BackColor = Color.White,
            })
            {
                var container = new Panel { Bounds = new Rectangle(0, 0, 100, 100), BackColor = Color.Green };
                var overflowing = new Panel { Bounds = new Rectangle(50, 10, 120, 30), BackColor = Color.Blue };
                container.Controls.Add(overflowing);
                form.Controls.Add(container);
                form.Show();
                Application.DoEvents();

                using (var bmp = new Bitmap(form.Width, form.Height))
                {
                    ZOrderedCapture.DrawToBitmap(form, bmp, new Rectangle(0, 0, form.Width, form.Height));
                    Assert.Equal(Color.Blue.ToArgb(), bmp.GetPixel(70, 20).ToArgb());   // child inside its parent
                    Assert.Equal(Color.White.ToArgb(), bmp.GetPixel(150, 20).ToArgb()); // clipped past the parent's edge
                }
            }
        }
    }
}
