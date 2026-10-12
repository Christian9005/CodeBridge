using CodeBridge.Flow;
using CodeBridge.Flow.Validation;

namespace CodeBridge.Core.Tests.Flow;

public class FlowValidatorTests
{
    [Fact]
    public void Validate_ValidBlinkStyleFlow_Passes()
    {
        var document = new FlowDocument
        {
            Name = "Blink Once",
            Nodes =
            {
                new FlowNode
                {
                    Id = "on",
                    Type = BuiltInBlockCatalog.ConstantBoolean,
                    Parameters = { ["value"] = true }
                },
                new FlowNode
                {
                    Id = "write",
                    Type = BuiltInBlockCatalog.GpioDigitalWrite,
                    Parameters = { ["pin"] = 2 }
                }
            },
            Connections =
            {
                new FlowConnection
                {
                    Id = "on-to-write",
                    FromNodeId = "on",
                    FromPort = "value",
                    ToNodeId = "write",
                    ToPort = "value"
                }
            }
        };

        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_UnknownBlockType_Fails()
    {
        var document = new FlowDocument
        {
            Nodes =
            {
                new FlowNode { Id = "unknown", Type = "missing.block" }
            }
        };

        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create());

        var issue = Assert.Single(result.Errors);
        Assert.Contains("Unknown block type", issue.Message);
    }

    [Fact]
    public void Validate_TypeMismatch_Fails()
    {
        var document = new FlowDocument
        {
            Nodes =
            {
                new FlowNode { Id = "timer", Type = BuiltInBlockCatalog.Timer },
                new FlowNode
                {
                    Id = "write",
                    Type = BuiltInBlockCatalog.GpioDigitalWrite,
                    Parameters = { ["pin"] = 2 }
                }
            },
            Connections =
            {
                new FlowConnection
                {
                    Id = "bad",
                    FromNodeId = "timer",
                    FromPort = "tick",
                    ToNodeId = "write",
                    ToPort = "value"
                }
            }
        };

        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create());

        Assert.Contains(result.Errors, issue => issue.Message.Contains("Cannot connect"));
    }

    [Fact]
    public void Validate_MissingRequiredInput_Fails()
    {
        var document = new FlowDocument
        {
            Nodes =
            {
                new FlowNode
                {
                    Id = "compare",
                    Type = BuiltInBlockCatalog.Compare
                }
            }
        };

        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create());

        Assert.Contains(result.Errors, issue => issue.Message.Contains("Required input 'left'"));
        Assert.Contains(result.Errors, issue => issue.Message.Contains("Required input 'right'"));
    }

    [Fact]
    public void Validate_Cycle_Fails()
    {
        var catalog = new FlowBlockCatalog()
            .Register(new FlowBlockDefinition
            {
                Type = "test.pass",
                DisplayName = "Pass",
                Ports =
                [
                    FlowPortDefinition.Input("in", FlowValueKind.Any, required: false),
                    FlowPortDefinition.Output("out", FlowValueKind.Any)
                ]
            });

        var document = new FlowDocument
        {
            Nodes =
            {
                new FlowNode { Id = "a", Type = "test.pass" },
                new FlowNode { Id = "b", Type = "test.pass" }
            },
            Connections =
            {
                new FlowConnection
                {
                    Id = "a-b",
                    FromNodeId = "a",
                    FromPort = "out",
                    ToNodeId = "b",
                    ToPort = "in"
                },
                new FlowConnection
                {
                    Id = "b-a",
                    FromNodeId = "b",
                    FromPort = "out",
                    ToNodeId = "a",
                    ToPort = "in"
                }
            }
        };

        var result = new FlowValidator().Validate(document, catalog);

        Assert.Contains(result.Errors, issue => issue.Message.Contains("cycle"));
    }

    [Fact]
    public void Validate_SampleChannelAbovePinRate_Fails()
    {
        var document = new FlowDocument
        {
            Nodes =
            {
                new FlowNode
                {
                    Id = "sample",
                    Type = BuiltInBlockCatalog.SampleChannel,
                    Parameters =
                    {
                        ["pin"] = 32,
                        ["analog"] = true,
                        ["mode"] = "HardwareTimer",
                        ["sampleRateHz"] = 6000,
                        ["bufferCapacity"] = 4096,
                        ["backpressure"] = "DropOldest",
                        ["batchSize"] = 64
                    }
                }
            }
        };

        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create());

        Assert.Contains(result.Errors, issue => issue.Message.Contains("exceeds GPIO 32 limit"));
    }

    [Fact]
    public void Validate_SampleChannelInterruptOnUnsupportedPin_Fails()
    {
        var document = new FlowDocument
        {
            Nodes =
            {
                new FlowNode
                {
                    Id = "sample",
                    Type = BuiltInBlockCatalog.SampleChannel,
                    Parameters =
                    {
                        ["pin"] = 2,
                        ["mode"] = "Interrupt",
                        ["sampleRateHz"] = 500,
                        ["bufferCapacity"] = 1024,
                        ["backpressure"] = "DropOldest",
                        ["batchSize"] = 32
                    }
                }
            }
        };

        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create());

        Assert.Contains(result.Errors, issue => issue.Message.Contains("does not support interrupt sampling"));
    }

    [Fact]
    public void Validate_SampleChannelWithinEsp32Limits_Passes()
    {
        var document = new FlowDocument
        {
            Nodes =
            {
                new FlowNode
                {
                    Id = "sample",
                    Type = BuiltInBlockCatalog.SampleChannel,
                    Parameters =
                    {
                        ["pin"] = 32,
                        ["analog"] = true,
                        ["mode"] = "HardwareTimer",
                        ["sampleRateHz"] = 5000,
                        ["bufferCapacity"] = 4096,
                        ["backpressure"] = "DropOldest",
                        ["batchSize"] = 128
                    }
                }
            }
        };

        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_DigitalWriteOnInputOnlyPin_Fails()
    {
        var document = new FlowDocument
        {
            Nodes =
            {
                new FlowNode
                {
                    Id = "write",
                    Type = BuiltInBlockCatalog.GpioDigitalWrite,
                    Parameters = { ["pin"] = 34 }
                }
            }
        };

        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create());

        Assert.Contains(result.Errors, issue => issue.Message.Contains("does not support digital output"));
    }

    [Fact]
    public void Validate_AnalogReadOnAdc2Pin_WarnsAboutWifi()
    {
        var document = new FlowDocument
        {
            Nodes =
            {
                new FlowNode
                {
                    Id = "analog",
                    Type = BuiltInBlockCatalog.GpioAnalogRead,
                    Parameters = { ["pin"] = 13 }
                }
            }
        };

        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create());

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, issue => issue.Message.Contains("ADC2"));
    }

    [Fact]
    public void Validate_InputOutputPinConflict_Fails()
    {
        var document = new FlowDocument
        {
            Nodes =
            {
                new FlowNode
                {
                    Id = "read",
                    Type = BuiltInBlockCatalog.GpioDigitalRead,
                    Parameters = { ["pin"] = 13 }
                },
                new FlowNode
                {
                    Id = "write",
                    Type = BuiltInBlockCatalog.GpioDigitalWrite,
                    Parameters = { ["pin"] = 13 }
                }
            }
        };

        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create());

        Assert.Contains(result.Errors, issue => issue.Message.Contains("conflicting uses"));
    }

    [Fact]
    public void Validate_PinModeOutputAndDigitalWriteSamePin_Passes()
    {
        var document = new FlowDocument
        {
            Nodes =
            {
                new FlowNode
                {
                    Id = "mode",
                    Type = BuiltInBlockCatalog.GpioPinMode,
                    Parameters = { ["pin"] = 13, ["mode"] = "Output" }
                },
                new FlowNode
                {
                    Id = "write",
                    Type = BuiltInBlockCatalog.GpioDigitalWrite,
                    Parameters = { ["pin"] = 13 }
                }
            }
        };

        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_UnknownBoardProfile_Fails()
    {
        var document = new FlowDocument
        {
            BoardId = "missing-board"
        };

        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create());

        Assert.Contains(result.Errors, issue => issue.Message.Contains("Unknown board profile"));
    }

    [Fact]
    public void Validate_ArduinoUnoServoOnRecommendedPin_Passes()
    {
        var document = new FlowDocument
        {
            BoardId = BuiltInBoardProfiles.ArduinoUno.Id,
            Nodes =
            {
                new FlowNode
                {
                    Id = "servo",
                    Type = BuiltInBlockCatalog.ServoWrite,
                    Parameters = { ["pin"] = 9, ["angle"] = 90 }
                }
            }
        };

        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create(BuiltInBoardProfiles.ArduinoUno));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_ArduinoUnoServoOnPwmButNotRecommendedPin_Fails()
    {
        var document = new FlowDocument
        {
            BoardId = BuiltInBoardProfiles.ArduinoUno.Id,
            Nodes =
            {
                new FlowNode
                {
                    Id = "servo",
                    Type = BuiltInBlockCatalog.ServoWrite,
                    Parameters = { ["pin"] = 3, ["angle"] = 90 }
                }
            }
        };

        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create(BuiltInBoardProfiles.ArduinoUno));

        Assert.Contains(result.Errors, issue => issue.Message.Contains("not a recommended servo/PWM pin"));
    }

    [Fact]
    public void Validate_ArduinoUnoAnalogPinAsDigitalOutput_Passes()
    {
        var document = new FlowDocument
        {
            BoardId = BuiltInBoardProfiles.ArduinoUno.Id,
            Nodes =
            {
                new FlowNode
                {
                    Id = "write",
                    Type = BuiltInBlockCatalog.GpioDigitalWrite,
                    Parameters = { ["pin"] = 14 }
                }
            }
        };

        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create(BuiltInBoardProfiles.ArduinoUno));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_ArduinoUnoHardwareTimerSampling_Fails()
    {
        var document = new FlowDocument
        {
            BoardId = BuiltInBoardProfiles.ArduinoUno.Id,
            Nodes =
            {
                new FlowNode
                {
                    Id = "sample",
                    Type = BuiltInBlockCatalog.SampleChannel,
                    Parameters =
                    {
                        ["pin"] = 14,
                        ["analog"] = true,
                        ["mode"] = "HardwareTimer",
                        ["sampleRateHz"] = 100,
                        ["bufferCapacity"] = 256,
                        ["backpressure"] = "DropOldest",
                        ["batchSize"] = 32
                    }
                }
            }
        };

        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create(BuiltInBoardProfiles.ArduinoUno));

        Assert.Contains(result.Errors, issue => issue.Message.Contains("does not expose hardware timer sampling"));
    }

    [Fact]
    public void Validate_ArduinoUnoPollingAnalogSampleWithinLimits_Passes()
    {
        var document = new FlowDocument
        {
            BoardId = BuiltInBoardProfiles.ArduinoUno.Id,
            Nodes =
            {
                new FlowNode
                {
                    Id = "sample",
                    Type = BuiltInBlockCatalog.SampleChannel,
                    Parameters =
                    {
                        ["pin"] = 14,
                        ["analog"] = true,
                        ["mode"] = "Polling",
                        ["sampleRateHz"] = 200,
                        ["bufferCapacity"] = 512,
                        ["backpressure"] = "DropOldest",
                        ["batchSize"] = 32
                    }
                }
            }
        };

        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create(BuiltInBoardProfiles.ArduinoUno));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_ServoWriteOnBootStrapPin_Fails()
    {
        var document = new FlowDocument
        {
            Nodes =
            {
                new FlowNode
                {
                    Id = "servo",
                    Type = BuiltInBlockCatalog.ServoWrite,
                    Parameters = { ["pin"] = 4, ["angle"] = 90 }
                }
            }
        };

        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create());

        Assert.Contains(result.Errors, issue => issue.Message.Contains("not a recommended servo/PWM pin"));
    }

    [Fact]
    public void Validate_ServoWriteOnRecommendedPin_Passes()
    {
        var document = new FlowDocument
        {
            Nodes =
            {
                new FlowNode
                {
                    Id = "servo",
                    Type = BuiltInBlockCatalog.ServoWrite,
                    Parameters = { ["pin"] = 13, ["angle"] = 90 }
                }
            }
        };

        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_PwmOutputOnMissingOrNonPwmPin_Fails()
    {
        foreach (var pin in new[] { 99, 34 }) // does not exist / input-only ESP32 pin
        {
            var document = new FlowDocument { BoardId = "esp32-devkit" };
            document.Nodes.Add(new FlowNode { Id = "pwm", Type = BuiltInBlockCatalog.PwmWrite, Parameters = { ["pin"] = pin } });

            var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create(BuiltInBoardProfiles.Esp32DevKit));

            Assert.False(result.IsValid, $"pin {pin} should be rejected");
        }
    }

    [Fact]
    public void Validate_MapWithAnEmptyInputRange_Fails()
    {
        var document = new FlowDocument { BoardId = "esp32-devkit" };
        document.Nodes.Add(new FlowNode { Id = "map", Type = BuiltInBlockCatalog.MathMap, Parameters = { ["inMin"] = 5, ["inMax"] = 5 } });

        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create(BuiltInBoardProfiles.Esp32DevKit));

        Assert.Contains(result.Errors, e => e.Message.Contains("input range is empty"));
    }
}
